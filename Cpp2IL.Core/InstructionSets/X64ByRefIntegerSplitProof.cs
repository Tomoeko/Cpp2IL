using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;
using IsilRegister = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Authenticates an ordered split of one by-value integer into two original managed references.</summary>
internal static class X64ByRefIntegerSplitProof
{
    internal const string EvidenceKey = "X64ByRefIntegerSplitProof";
    internal const string LowRegister = "byref_integer_split_low";
    internal const string ShiftRegister = "byref_integer_split_shift";
    internal const string HighRegister = "byref_integer_split_high";
    internal sealed record Shape(NativeInstruction LowStore, NativeInstruction Shift,
        NativeInstruction HighStore, NativeInstruction Return, NativeRegister Source,
        NativeRegister LowPointer, NativeRegister HighPointer, bool Arithmetic);
    internal sealed class InputState(List<object> values, byte[] bytes)
    {
        private readonly object[] _values = values.ToArray();
        private readonly byte[] _bytes = bytes;
        internal bool Matches(InputState other) => _values.SequenceEqual(other._values) && _bytes.SequenceEqual(other._bytes);
    }
    internal sealed record Proof(Shape Native, InputState Input)
    {
        internal bool Matches(Proof other) => Native == other.Native && Input.Matches(other.Input);
    }
    internal static Proof? GetEvidence(MethodAnalysisContext method) => method.GetExtraData<Proof>(EvidenceKey);
    internal static bool WasLifted(MethodAnalysisContext method) => NativeRecoveryProofTracker.Has(method, EvidenceKey);

    internal static List<IsilInstruction>? TryLift(MethodAnalysisContext method)
    {
        if (Find(method) is not { } proof)
            return null;
        method.PutExtraData(EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var low = new IsilRegister(null, LowRegister);
        var shifted = new IsilRegister(null, ShiftRegister);
        var high = new IsilRegister(null, HighRegister);
        var source = Register(proof.Native.Source);
        // Each destination keeps its original byref element type and direction.
        // The two writes stay separate and ordered, including when callers alias them.
        return
        [
            new(0, OpCode.IntegerExtend, low, source, new Immediate(32), new Immediate(32), Signed(method, 1))
                { NativeAddress = proof.Native.LowStore.IP },
            new(1, OpCode.Move, new ISIL.MemoryOperand(Register(proof.Native.LowPointer), null, 0), low)
                { NativeAddress = proof.Native.LowStore.IP, IntegerBitWidth = 32 },
            new(2, proof.Native.Arithmetic ? OpCode.ShiftRight : OpCode.ShiftRightUnsigned, shifted, source, new Immediate(32))
                { NativeAddress = proof.Native.Shift.IP, IntegerBitWidth = 64 },
            new(3, OpCode.IntegerExtend, high, shifted, new Immediate(32), new Immediate(32), Signed(method, 2))
                { NativeAddress = proof.Native.HighStore.IP },
            new(4, OpCode.Move, new ISIL.MemoryOperand(Register(proof.Native.HighPointer), null, 0), high)
                { NativeAddress = proof.Native.HighStore.IP, IntegerBitWidth = 32 },
            new(5, OpCode.Return) { NativeAddress = proof.Native.Return.IP },
        ];
    }

    private static Immediate Signed(MethodAnalysisContext method, int ordinal) => new(
        ReferenceEquals(((ByRefTypeAnalysisContext)method.Parameters[ordinal].ParameterType).ElementType,
            method.AppContext.SystemTypes.SystemInt32Type) ? 1 : 0);
    private static IsilRegister Register(NativeRegister native) => new(null, native.ToString().ToLowerInvariant());

    internal static Proof? Find(MethodAnalysisContext? method)
    {
        if (method == null || !Signature(method))
            return null;
        try
        {
            var body = X64NativeInstructionReader.ReadFramelessLeaf(method, 4, 32) ??
                       X64NativeInstructionReader.ReadFramelessLeaf(method, 5, 32);
            if (body == null || TryProveShape(body) is not { } shape)
                return null;
            // The resolver includes the entry return-address adjustment for stack
            // MethodInfo. This closed body consumes only the three register arguments.
            var arguments = new X64CallingConventionResolver().ResolveForParameters(method);
            var first = method.IsStatic ? 0 : 1;
            if (arguments.Length != first + 4 ||
                arguments[first] is not IsilRegister source || source != Register(shape.Source) ||
                arguments[first + 1] is not IsilRegister low || low != Register(shape.LowPointer) ||
                arguments[first + 2] is not IsilRegister high || high != Register(shape.HighPointer) ||
                (method.IsStatic ? arguments[first + 3] is not IsilRegister { Name: "r9", Version: -1 } :
                    arguments[first + 3] is not StackOffset) ||
                !method.IsStatic && arguments[0] is not IsilRegister { Name: "rcx", Version: -1 })
                return null;
            // Native SHR and SAR bind their own managed shift, independently of
            // the expected destination types. Both conversions observe low32 only.
            if (shape.Arithmetic && !ReferenceEquals(method.Parameters[0].ParameterType, method.AppContext.SystemTypes.SystemInt64Type))
                return null;
            var length = checked((int)(shape.Return.NextIP - method.UnderlyingPointer));
            return new Proof(shape, new InputState(Snapshot(method), method.RawBytes.AsSpan().Slice(0, length).ToArray()));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is not (4 or 5) || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any() ||
            body.Count == 5 && body[0] is not { Code: Code.Nopw or Code.Nopd, OpCount: 0 })
            return null;
        var low = body[^4];
        var shift = body[^3];
        var high = body[^2];
        var ret = body[^1];
        if (!Store(low) || !Store(high) || low.Op1Register != high.Op1Register ||
            shift.Code is not (Code.Shr_rm64_imm8 or Code.Sar_rm64_imm8) ||
            shift.Op0Kind != OpKind.Register || shift.Op0Register != low.Op1Register.GetFullRegister() ||
            shift.Op1Kind != OpKind.Immediate8 || shift.Immediate8 != 32 ||
            ret.Code != Code.Retnq || ret.OpCount != 0)
            return null;
        var source = shift.Op0Register;
        var lowPointer = low.MemoryBase;
        var highPointer = high.MemoryBase;
        // These are independently mapped to three original Win64 argument slots.
        // In particular, shifting a pointer slot cannot retain its destination.
        return source != lowPointer && source != highPointer && lowPointer != highPointer
            ? new Shape(low, shift, high, ret, source, lowPointer, highPointer, shift.Code == Code.Sar_rm64_imm8)
            : null;
    }

    private static bool Store(NativeInstruction instruction) => instruction.Code == Code.Mov_rm32_r32 &&
        instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Memory && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register.GetSize() == 4 && VolatileArgument(instruction.Op1Register.GetFullRegister()) &&
        VolatileArgument(instruction.MemoryBase) && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryIndexScale == 1 && instruction.MemoryDisplacement64 == 0 && instruction.MemorySize.GetSize() == 4;
    private static bool VolatileArgument(NativeRegister register) => register is NativeRegister.RCX or NativeRegister.RDX or
        NativeRegister.R8 or NativeRegister.R9;

    private static bool Signature(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not PE || !X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            method.Definition is not { GenericContainer: null, parameterCount: 3 } definition ||
            method.DeclaringType is not { Definition: { GenericContainer: null, RawType:
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) || method.Parameters.Count != 3 ||
            definition.InternalParameterData is not [var value, var low, var high] ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName || method.IsVirtual ||
            method.GenericParameters.Count != 0 || method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            method.OverrideReturnType != null || !ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemVoidType) ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.BaseMethod != null || method.Overrides.Count != 0 || owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 || owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes || !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            method.UnderlyingPointer == 0 || !method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings.Count(candidate => ReferenceEquals(candidate, method)) != 1)
            return false;
        var raw = new[] { value, low, high };
        for (var i = 0; i < 3; i++)
        {
            var parameter = method.Parameters[i];
            if (!ReferenceEquals(parameter.Definition, raw[i]) || parameter.ParameterIndex != i ||
                !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.OverrideParameterType != null ||
                parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
                parameter.OverrideAttributes != null || parameter.UseOverrideDefaultValue ||
                raw[i].RawType is not { NumMods: 0, Pinned: 0 } type)
                return false;
            if (i == 0)
            {
                if (type.Byref != 0 || type.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8) ||
                    parameter.Attributes != ParameterAttributes.None || !CanonicalWide(parameter.ParameterType, method) ||
                    !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) || parameter.ParameterType.Type != type.Type)
                    return false;
            }
            else if (type.Byref != 1 || type.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4) ||
                     parameter.Attributes is not (ParameterAttributes.None or ParameterAttributes.Out) ||
                     parameter.ParameterType is not ByRefTypeAnalysisContext current ||
                     parameter.DefaultParameterType is not ByRefTypeAnalysisContext original ||
                     !CanonicalWord(current.ElementType, method) || !ReferenceEquals(current.ElementType, original.ElementType) ||
                     current.ElementType.Type != type.Type)
                return false;
        }
        return true;
    }

    internal static bool CanonicalWide(TypeAnalysisContext? type, MethodAnalysisContext method) =>
        ReferenceEquals(type, method.AppContext.SystemTypes.SystemInt64Type) || ReferenceEquals(type, method.AppContext.SystemTypes.SystemUInt64Type);
    internal static bool CanonicalWord(TypeAnalysisContext? type, MethodAnalysisContext method) =>
        ReferenceEquals(type, method.AppContext.SystemTypes.SystemInt32Type) || ReferenceEquals(type, method.AppContext.SystemTypes.SystemUInt32Type);

    private static List<object> Snapshot(MethodAnalysisContext method)
    {
        var definition = method.Definition!;
        var owner = method.DeclaringType!;
        var type = owner.Definition!;
        var values = new List<object>
        {
            method.UnderlyingPointer, method.Name, method.Attributes, method.ImplAttributes, method.ReturnType,
            definition.nameIndex, definition.token, definition.flags, definition.iflags, definition.slot,
            definition.returnTypeIdx, definition.declaringTypeIdx, definition.parameterStart, definition.parameterCount,
            owner, owner.Name, owner.Namespace, owner.Attributes, owner.BaseType!, type.NameIndex, type.NamespaceIndex,
            type.Token, type.Bitfield, type.ByvalTypeIndex, type.ParentIndex,
        };
        Capture(type.RawType, values);
        Capture(definition.RawReturnType!, values);
        foreach (var parameter in method.Parameters)
        {
            var raw = parameter.Definition!;
            values.AddRange([parameter, parameter.Name, parameter.Attributes, parameter.ParameterIndex,
                parameter.ParameterType is ByRefTypeAnalysisContext reference ? reference.ElementType : parameter.ParameterType,
                raw.nameIndex, raw.token, raw.typeIndex]);
            Capture(raw.RawType!, values);
        }
        return values;
    }
    private static void Capture(Il2CppType type, List<object> values) => values.AddRange(
        [type.Bits, type.Datapoint, type.Data.Dummy, type.Attrs, type.Type, type.NumMods, type.Byref, type.Pinned, type.ValueType]);
}
