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
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Authenticates a complete binary64 narrowing, binary32 division and optional ordered-negative selection.</summary>
internal static class X64ScalarFloatConversionCompositionProof
{
    internal const string EvidenceKey = "X64ScalarFloatConversionCompositionProof";
    internal sealed record Shape(NativeInstruction? FieldLoad, NativeInstruction Conversion,
        NativeInstruction Division, NativeInstruction? Comparison, NativeInstruction? Branch,
        NativeInstruction? SignFlip, NativeInstruction? ReturnCopy, NativeInstruction Return);

    internal sealed class InputState(List<object> values)
    {
        private readonly object[] _values = values.ToArray();
        internal bool Matches(InputState other) => _values.SequenceEqual(other._values);
    }

    internal sealed record Proof(Shape Native, NativeInstruction[] Body, int SourceParameter,
        int DivisorParameter, FieldAnalysisContext? Field, InputState Input)
    {
        internal bool Matches(Proof other) => Native == other.Native && Body.SequenceEqual(other.Body) &&
            SourceParameter == other.SourceParameter && DivisorParameter == other.DivisorParameter &&
            ReferenceEquals(Field, other.Field) && Input.Matches(other.Input);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Proof>(EvidenceKey) != null;

    internal static List<ManagedInstruction>? TryLift(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> native)
    {
        if (!native.Take(12).Any(instruction => instruction.Code == Code.Divss_xmm_xmmm32) ||
            Find(method) is not { } proof || !native.Take(proof.Body.Length).SequenceEqual(proof.Body))
            return null;
        method.PutExtraData(EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var instructions = new List<ManagedInstruction>();
        var source = proof.Field == null
            ? new ManagedRegister(null, X86Utils.GetRegisterName(proof.Native.Conversion.Op1Register))
            : new ManagedRegister(null, "scalar_composition_field_capture");
        if (proof.Field != null)
            instructions.Add(new(instructions.Count, OpCode.Move, source,
                new ISIL.MemoryOperand(new ManagedRegister(null, "rcx"), null, proof.Field.Offset))
                { NativeAddress = proof.Native.FieldLoad!.Value.IP });
        var narrowed = new ManagedRegister(null, "scalar_composition_narrowed");
        instructions.Add(new(instructions.Count, OpCode.FloatConvert, narrowed, source, new Immediate(64), new Immediate(32))
            { NativeAddress = proof.Native.Conversion.IP });
        var quotient = new ManagedRegister(null, "scalar_composition_quotient");
        instructions.Add(new(instructions.Count, OpCode.FloatDivide, quotient, narrowed,
            new ManagedRegister(null, X86Utils.GetRegisterName(proof.Native.Division.Op1Register)), new Immediate(32))
            { NativeAddress = proof.Native.Division.IP });
        var returned = quotient;
        if (proof.Native.SignFlip is { } signFlip)
        {
            returned = new ManagedRegister(null, "scalar_composition_selected");
            instructions.Add(new(instructions.Count, OpCode.FloatNegateNegative, returned, quotient, new Immediate(32))
                { NativeAddress = signFlip.IP });
        }
        instructions.Add(new(instructions.Count, OpCode.Return, returned) { NativeAddress = proof.Native.Return.IP });
        return instructions;
    }

    internal static Proof? Find(MethodAnalysisContext method)
    {
        try
        {
            if (!Signature(method) || method.AppContext.Binary is not PE pe ||
                X64UnwindProof.ForApplication(method.AppContext) is not { } unwind)
                return null;
            if (method.RawBytes.Length == 0) method.EnsureRawBytes();
            var prefix = X86Utils.Iterate(method).Take(12).ToArray();
            var returnIndex = Array.FindIndex(prefix, instruction => instruction.Code == Code.Retnq);
            if (returnIndex < 0 || TryProveShape(prefix.Take(returnIndex + 1).ToArray()) is not { } shape ||
                X64NativeInstructionReader.ReadFramelessBody(method, returnIndex + 1, 64) is not { } body ||
                TryProveShape(body) != shape ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null ||
                shape.SignFlip is { } flip && !HasSignMask(pe, unwind, flip))
                return null;
            var divisor = ParameterFor(method, shape.Division.Op1Register, method.AppContext.SystemTypes.SystemSingleType);
            var source = shape.FieldLoad == null
                ? ParameterFor(method, shape.Conversion.Op1Register, method.AppContext.SystemTypes.SystemDoubleType) : -1;
            FieldAnalysisContext? field = null;
            if (divisor < 0 || shape.FieldLoad == null && source < 0)
                return null;
            if (shape.FieldLoad is { } load)
            {
                if (method.IsStatic || method.DeclaringType!.Fields.Where(candidate => !candidate.IsStatic &&
                        candidate.Offset == (int)load.MemoryDisplacement64).ToArray() is not [var loaded] ||
                    !ReferenceEquals(loaded.FieldType, method.AppContext.SystemTypes.SystemDoubleType) ||
                    loaded.BackingData?.Field.RawFieldType is not
                        { Type: Il2CppTypeEnum.IL2CPP_TYPE_R8, NumMods: 0, Byref: 0, Pinned: 0 } ||
                    loaded.Name != loaded.DefaultName || !HasFieldLayout(method, loaded))
                    return null;
                field = loaded;
            }
            var values = Snapshot(method);
            return values == null ? null : new Proof(shape, body, source, divisor, field, values);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        if (!NativeRecoveryProofTracker.Has(method, EvidenceKey) ||
            method.GetExtraData<Proof>(EvidenceKey) is not { } original || Find(method) is not { } current ||
            !original.Matches(current) || !NativeStraightLineGraph.TryGetBody(method, out var instructions))
            return false;
        var index = 0;
        LocalVariable source;
        if (original.Field is { } field)
        {
            if (instructions.Count == 0 || instructions[index++] is not
                    { OpCode: OpCode.Move, Operands: [LocalVariable capture, FieldReference read] } load ||
                !Plain(load) || load.NativeAddress != original.Native.FieldLoad!.Value.IP ||
                !ReferenceEquals(read.Field, field) || !HasFieldLayout(method, field) ||
                read.Offset != field.Offset || !ReferenceEquals(read.Local.Type, method.DeclaringType) ||
                !read.Local.IsThis || !method.ParameterLocals.Contains(read.Local) ||
                read.Local.Register != new ManagedRegister(null, "rcx") ||
                !ReferenceEquals(capture.Type, method.AppContext.SystemTypes.SystemDoubleType) ||
                method.ParameterLocals.Contains(capture))
                return false;
            source = capture;
        }
        else
        {
            if (instructions.Count <= index || instructions[index].Operands is not [_, LocalVariable incoming, _, _] ||
                !Incoming(method, incoming, original.SourceParameter))
                return false;
            source = incoming;
        }
        if (instructions.Count <= index || instructions[index++] is not { } conversion ||
            !FloatConversion.TryGet(conversion, out var converted) || converted != new FloatConversion(64, 32) ||
            conversion.NativeAddress != original.Native.Conversion.IP ||
            conversion.Operands is not [LocalVariable narrowed, var input, _, _] ||
            !ReferenceEquals(source, input) || !ReferenceEquals(source.Type, method.AppContext.SystemTypes.SystemDoubleType) ||
            !Output(method, narrowed, source))
            return false;
        if (instructions.Count <= index || instructions[index++] is not { } division ||
            !FloatDivision.TryGet(division, out var divided) || divided.Width != 32 ||
            division.NativeAddress != original.Native.Division.IP || division.Operands is not
                [LocalVariable quotient, var numerator, LocalVariable divisor, _] ||
            !ReferenceEquals(numerator, narrowed) || !Incoming(method, divisor, original.DivisorParameter) ||
            !ReferenceEquals(divisor.Type, method.AppContext.SystemTypes.SystemSingleType) ||
            !Output(method, quotient, narrowed, divisor, source))
            return false;
        var result = quotient;
        if (original.Native.SignFlip is { } flip)
        {
            if (instructions.Count <= index || instructions[index++] is not { } selection ||
                !FloatNegativeSelection.TryGet(selection, out var selected) || selected.Width != 32 ||
                selection.NativeAddress != flip.IP || selection.Operands is not [LocalVariable chosen, var value, _] ||
                !ReferenceEquals(value, quotient) || !Output(method, chosen, source, narrowed, quotient, divisor))
                return false;
            result = chosen;
        }
        return instructions.Count == index + 1 && instructions[index] is
                   { OpCode: OpCode.Return, Operands: [LocalVariable returned] } ret &&
               Plain(ret) && ret.NativeAddress == original.Native.Return.IP && ReferenceEquals(returned, result);
    }

    private static bool Plain(ManagedInstruction instruction) =>
        instruction.IntegerBitWidth == 0 && instruction.CallSemantics == CallSemantics.Direct;

    private static bool Incoming(MethodAnalysisContext method, LocalVariable local, int index) =>
        method.ParameterLocals.Contains(local) && LocalVariables.GetIncomingParameterIndex(method, local) == index &&
        ReferenceEquals(local.Type, method.Parameters[index].ParameterType);

    private static bool Output(MethodAnalysisContext method, LocalVariable result, params LocalVariable[] inputs) =>
        !method.ParameterLocals.Contains(result) && inputs.All(input => !ReferenceEquals(input, result)) &&
        ReferenceEquals(result.Type, method.AppContext.SystemTypes.SystemSingleType);

    private static bool HasFieldLayout(MethodAnalysisContext method, FieldAnalysisContext field) =>
        ReferenceEquals(field.DeclaringType, method.DeclaringType) &&
        NarrowFieldEqualityProof.HasUnchangedFloatingFieldLayout(new FieldReference(field,
            new LocalVariable("receiver", new ManagedRegister(null, "rcx"), method.DeclaringType!), field.Offset), 64);

    private static int ParameterFor(MethodAnalysisContext method, NativeRegister native, TypeAnalysisContext expected)
    {
        var abi = new X64CallingConventionResolver().ResolveForParameters(method);
        var offset = method.IsStatic ? 0 : 1;
        var matches = method.Parameters.Where(parameter =>
            abi[parameter.ParameterIndex + offset] is ManagedRegister register &&
            register.Name == X86Utils.GetRegisterName(native) && ReferenceEquals(parameter.ParameterType, expected)).ToArray();
        return matches is [var match] ? match.ParameterIndex : -1;
    }

    private static bool Signature(MethodAnalysisContext method)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            method.DeclaringType is not { Definition: { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes || !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            method.Definition is not { GenericContainer: null, IsUnmanagedCallersOnly: false,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_R4, NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemSingleType) ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            method.OverrideReturnType != null || !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) || method.Parameters.Count > (method.IsStatic ? 4 : 3))
            return false;
        foreach (var parameter in method.Parameters)
            if (!ReferenceEquals(parameter.DeclaringMethod, method) || parameter.IsRef ||
                parameter.OverrideParameterType != null || parameter.UseOverrideDefaultValue ||
                parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
                !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
                parameter.Definition?.RawType is not { NumMods: 0, Byref: 0, Pinned: 0 } raw ||
                raw.Type != parameter.ParameterType.Type || parameter.ParameterType.IsGenericInstance ||
                parameter.ParameterType.GenericParameters.Count != 0)
                return false;
        // Positional Win64 arguments use one slot each, including an unused
        // ordinary by-value aggregate. Only actually consumed scalar slots bind
        // numeric types; an ignored prefix keeps its original declaration.
        return !new X64CallingConventionResolver().ReturnsViaHiddenBuffer(method);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is < 3 or > 10 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any())
            return null;
        var position = 0;
        NativeInstruction? load = null;
        if (body[position].Code == Code.Movsd_xmm_xmmm64)
        {
            var read = body[position++];
            if (read.Op0Kind != OpKind.Register || read.Op0Register is not (NativeRegister.XMM0 or NativeRegister.XMM1) ||
                read.Op1Kind != OpKind.Memory || read.MemoryBase != NativeRegister.RCX ||
                read.MemoryIndex != NativeRegister.None || read.MemoryIndexScale != 1 ||
                read.MemorySize.GetSize() != 8 || read.MemoryDisplacement64 is < 16 or >= 4096 ||
                read.MemoryDisplacement64 % 8 != 0 || read.FlowControl != FlowControl.Next)
                return null;
            load = read;
        }
        var cleared = new HashSet<NativeRegister>();
        while (position < body.Count && IsZero(body[position]))
        {
            var zero = body[position++];
            if (!cleared.Add(zero.Op0Register) || load?.Op0Register == zero.Op0Register)
                return null;
        }
        if (position + 2 >= body.Count) return null;
        var conversion = body[position++];
        var value = conversion.Op0Register;
        if (conversion.OpCount != 2 || conversion.Op0Kind != OpKind.Register ||
            value is not (NativeRegister.XMM0 or NativeRegister.XMM1) || conversion.Op1Kind != OpKind.Register ||
            conversion.FlowControl != FlowControl.Next ||
            (load == null
                ? conversion.Code != Code.Cvtsd2ss_xmm_xmmm64 || conversion.Length != 4 ||
                  conversion.Op1Register is < NativeRegister.XMM0 or > NativeRegister.XMM3 ||
                  cleared.Contains(conversion.Op1Register)
                : conversion.Code != Code.Cvtpd2ps_xmm_xmmm128 || conversion.Length != 4 ||
                  conversion.Op1Register != load.Value.Op0Register || value != load.Value.Op0Register))
            return null;
        // MOVSD m64 clears the second binary64 lane. The subsequent packed
        // conversion therefore has an exact +0 second result; no upper lane
        // reaches memory, a call, another arithmetic lane or the scalar return.
        var divide = body[position++];
        if (!Registers(divide, Code.Divss_xmm_xmmm32, value, divide.Op1Register) || divide.Length != 4 ||
            divide.Op1Register is < NativeRegister.XMM0 or > NativeRegister.XMM3 ||
            divide.Op1Register == value || cleared.Contains(divide.Op1Register) ||
            load?.Op0Register == divide.Op1Register)
            return null;
        NativeInstruction? comparison = null, branch = null, signFlip = null, copy = null;
        if (position < body.Count && body[position].Code == Code.Comiss_xmm_xmmm32)
        {
            var compare = body[position++];
            if (compare.Op0Register == value || !cleared.Contains(compare.Op0Register) ||
                !Registers(compare, Code.Comiss_xmm_xmmm32, compare.Op0Register, value) || position + 2 >= body.Count)
                return null;
            var jump = body[position++];
            var flip = body[position++];
            if (jump.Code is not (Code.Jbe_rel8_64 or Code.Jbe_rel32_64) || jump.OpCount != 1 ||
                jump.Op0Kind != OpKind.NearBranch64 || flip.Code != Code.Xorps_xmm_xmmm128 || flip.OpCount != 2 ||
                flip.Op0Kind != OpKind.Register || flip.Op0Register != value || flip.Op1Kind != OpKind.Memory ||
                !flip.IsIPRelativeMemoryOperand || flip.MemoryBase != NativeRegister.RIP ||
                flip.MemoryIndex != NativeRegister.None || flip.MemorySize.GetSize() != 16 ||
                flip.Length != 7 || flip.IPRelativeMemoryAddress % 16 != 0 || flip.FlowControl != FlowControl.Next ||
                jump.NearBranchTarget != body[position].IP)
                return null;
            comparison = compare; branch = jump; signFlip = flip;
        }
        if (value != NativeRegister.XMM0)
        {
            if (position >= body.Count || !Registers(body[position], Code.Movaps_xmm_xmmm128, NativeRegister.XMM0, value) ||
                body[position].Length != 3)
                return null;
            copy = body[position++];
        }
        if (position != body.Count - 1 || body[position] is not { Code: Code.Retnq, Length: 1, OpCount: 0 } returned)
            return null;
        return new(load, conversion, divide, comparison, branch, signFlip, copy, returned);
    }

    private static bool IsZero(NativeInstruction instruction) => instruction.Op0Register is
        NativeRegister.XMM0 or NativeRegister.XMM1 && instruction.Length == 3 &&
        Registers(instruction, Code.Xorps_xmm_xmmm128, instruction.Op0Register, instruction.Op0Register);

    private static bool Registers(NativeInstruction instruction, Code code, NativeRegister left, NativeRegister right) =>
        instruction.Code == code && instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind == OpKind.Register && instruction.Op0Register == left &&
        instruction.Op1Register == right && instruction.FlowControl == FlowControl.Next;

    private static bool HasSignMask(PE pe, X64UnwindProof.Index unwind, NativeInstruction flip)
    {
        var address = flip.IPRelativeMemoryAddress;
        var offset = unwind.MapReadOnlyData(address, 16);
        if (offset < 0 || address % 16 != 0 || !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, address, 16))
            return false;
        var mask = pe.GetRawBinaryContent().Slice(offset, 16);
        for (var lane = 0; lane < 4; lane++)
            if (mask[lane * 4] != 0 || mask[lane * 4 + 1] != 0 || mask[lane * 4 + 2] != 0 || mask[lane * 4 + 3] != 0x80)
                return false;
        return true;
    }

    private static InputState? Snapshot(MethodAnalysisContext method)
    {
        var definition = method.Definition!;
        var values = new List<object>
        {
            method.UnderlyingPointer, method.Name, method.Attributes, method.ImplAttributes, method.ReturnType,
            definition.nameIndex, definition.token, definition.flags, definition.iflags, definition.slot,
            definition.returnTypeIdx, definition.declaringTypeIdx, definition.parameterStart, definition.parameterCount,
        };
        CaptureRaw(definition.RawReturnType!, values);
        if (!CaptureType(method.DeclaringType!, values)) return null;
        foreach (var parameter in method.Parameters)
        {
            var raw = parameter.Definition!;
            values.AddRange([parameter, parameter.Name, parameter.Attributes, parameter.ParameterIndex,
                parameter.ParameterType, raw.nameIndex, raw.token, raw.typeIndex]);
            CaptureRaw(raw.RawType!, values);
            if (!CaptureType(parameter.ParameterType, values)) return null;
        }
        return new(values);
    }

    private static bool CaptureType(TypeAnalysisContext type, List<object> values)
    {
        if (type.Name != type.DefaultName || type.Namespace != type.DefaultNamespace ||
            type.Attributes != type.DefaultAttributes || !ReferenceEquals(type.BaseType, type.DefaultBaseType))
            return false;
        values.AddRange([type, type.Name, type.Namespace, type.Attributes]);
        if (type.Definition is not { } definition) return !type.IsValueType;
        values.AddRange([definition.NameIndex, definition.NamespaceIndex, definition.Token, definition.Bitfield,
            definition.ByvalTypeIndex, definition.ParentIndex, definition.RawSizes.instance_size, definition.FieldCount]);
        CaptureRaw(definition.RawType, values);
        if (type.Fields.Count != definition.FieldCount) return false;
        foreach (var field in type.Fields)
        {
            if (field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes ||
                field.Offset != field.DefaultOffset || field.OverrideFieldType != null ||
                field.BackingData?.Field.RawFieldType is not { } raw)
                return false;
            values.AddRange([field, field.Name, field.Attributes, field.Offset, field.FieldType,
                field.BackingData.Field.nameIndex, field.BackingData.Field.token, field.BackingData.Field.typeIndex]);
            CaptureRaw(raw, values);
        }
        return true;
    }

    private static void CaptureRaw(Il2CppType type, List<object> values) => values.AddRange(
        [type.Bits, type.Datapoint, type.Data.Dummy, type.Attrs, type.Type, type.NumMods, type.Byref, type.Pinned, type.ValueType]);
}
