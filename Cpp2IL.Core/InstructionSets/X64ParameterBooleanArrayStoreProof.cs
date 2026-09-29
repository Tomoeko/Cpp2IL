using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// A complete parameter-origin Boolean array store. Pure register setup may
/// precede either guard; the successful path has exactly one byte store. The
/// managed array operation preserves the ordered null and unsigned bounds exits.
/// </summary>
internal static class X64ParameterBooleanArrayStoreProof
{
    private const string EvidenceKey = "X64ParameterBooleanArrayStoreProof.Store";
    private static readonly Register[] ArgumentRegisters =
        [Register.RCX, Register.RDX, Register.R8, Register.R9];

    internal sealed record Shape(int InstructionCount, Register ArrayEntry,
        Register IndexEntry, Register ValueEntry, int? Literal,
        int NullTest, int BoundsCompare, int Store, int Return,
        int NullCall, int BoundsCall);

    internal sealed record Evidence(Shape Shape,
        ParameterAnalysisContext ArrayParameter, ParameterAnalysisContext IndexParameter,
        ParameterAnalysisContext? ValueParameter, ulong StoreAddress, ulong ReturnAddress);

    private enum ValueKind { Entry, Low32, SignedIndex, Literal }
    private sealed record Value(Register Origin, ValueKind Kind, int Literal = 0);

    internal static Evidence? GetEvidence(MethodAnalysisContext method) =>
        method.GetExtraData<Evidence>(EvidenceKey);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<Instruction> decoded)
    {
        if (Find(method, decoded) is not { } evidence)
            return null;
        var array = new ISIL.Register(null, X86Utils.GetRegisterName(evidence.Shape.ArrayEntry));
        var index = new ISIL.Register(null, X86Utils.GetRegisterName(evidence.Shape.IndexEntry));
        ISIL.IOperand value = evidence.Shape.Literal is { } literal
            ? new ISIL.Immediate(literal)
            : new ISIL.Register(null, X86Utils.GetRegisterName(evidence.Shape.ValueEntry));
        method.PutExtraData(EvidenceKey, evidence);
        return
        [
            new(0, ISIL.OpCode.Move,
                new ISIL.MemoryOperand(array, index,
                    Il2CppArrayUtils.GetFirstItemOffset(method.AppContext.Binary), 1), value)
                { NativeAddress = evidence.StoreAddress },
            new(1, ISIL.OpCode.Return) { NativeAddress = evidence.ReturnAddress },
        ];
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<Instruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
                method.Definition is not { GenericContainer: null,
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
                method.DeclaringType is not { IsValueType: false, IsInterface: false,
                    IsGenericInstance: false, Definition: { GenericContainer: null,
                        RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                            NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
                !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
                owner.GenericParameters.Count != 0 || owner.Name != owner.DefaultName ||
                owner.Namespace != owner.DefaultNamespace ||
                owner.Attributes != owner.DefaultAttributes ||
                !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
                method.IsVirtual || !method.IsVoid || method.OverrideReturnType != null ||
                !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) ||
                method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
                method.Attributes != method.DefaultAttributes ||
                method.ImplAttributes != method.DefaultImplAttributes ||
                method.GenericParameters.Count != 0 ||
                (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                    MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
                method.Parameters.Count is < 2 or > 3 ||
                decoded.Count < 12 || decoded[0].IP != method.UnderlyingPointer ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var aliases) ||
                aliases.Count(candidate => ReferenceEquals(candidate, method)) != 1)
                return null;

            var shape = TryProveShape(decoded,
                3L * app.Binary.PointerSizeBytes,
                Il2CppArrayUtils.GetFirstItemOffset(app.Binary));
            if (shape == null ||
                X64Stack28BodyProof.Read(method, shape.InstructionCount, 128) is not { } closed ||
                !closed.SequenceEqual(decoded.Take(shape.InstructionCount)) ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind,
                    method.UnderlyingPointer,
                    checked((uint)(closed[^1].NextIP - method.UnderlyingPointer))))
                return null;
            ParameterAnalysisContext? array = null, index = null, value = null;
            for (var position = 0; position < method.Parameters.Count; position++)
            {
                var parameter = method.Parameters[position];
                var slot = position + (method.IsStatic ? 0 : 1);
                if (slot >= ArgumentRegisters.Length ||
                    parameter.ParameterIndex != position ||
                    !ReferenceEquals(parameter.DeclaringMethod, method) ||
                    parameter.Definition == null || parameter.OverrideParameterType != null ||
                    parameter.IsRef || parameter.Name != parameter.DefaultName ||
                    parameter.Attributes != parameter.DefaultAttributes ||
                    parameter.Definition.RawType is not { NumMods: 0, Byref: 0, Pinned: 0 } raw)
                    return null;
                var entry = ArgumentRegisters[slot];
                if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY &&
                    parameter.ParameterType is SzArrayTypeAnalysisContext arrayType &&
                    raw.GetEncapsulatedType() is { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                        NumMods: 0, Byref: 0, Pinned: 0 } &&
                    ReferenceEquals(arrayType.ElementType, app.SystemTypes.SystemBooleanType) &&
                    array == null && entry == shape.ArrayEntry)
                    array = parameter;
                else if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_I4 &&
                         ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt32Type) &&
                         index == null && entry == shape.IndexEntry)
                    index = parameter;
                else if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN &&
                         ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemBooleanType) &&
                         value == null && (shape.Literal != null || entry == shape.ValueEntry))
                    value = parameter;
                else
                    return null;
            }
            if (array == null || index == null || shape.Literal == null && value == null ||
                X86RuntimeNullThrowProof.TryIdentify(app, closed[shape.NullCall].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app, closed[shape.BoundsCall].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, closed,
                    new HashSet<ulong> { closed[shape.NullCall].IP, closed[shape.BoundsCall].IP }) != null)
                return null;
            return new Evidence(shape, array, index, shape.Literal == null ? value : null,
                closed[shape.Store].IP, closed[shape.Return].IP);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body,
        long lengthOffset = 0x18, long dataOffset = 0x20)
    {
        if (body.Count < 12 || body[0].Length != 4 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub))
            return null;
        var values = ArgumentRegisters.ToDictionary(register => register,
            register => new Value(register, ValueKind.Entry));
        var cursor = 1;
        var setupCount = 0;
        bool Setup()
        {
            while (cursor < body.Count && TrySetup(body[cursor], values))
            {
                if (++setupCount > 4)
                    return false;
                cursor++;
            }
            return true;
        }
        if (!Setup() || cursor + 1 >= body.Count)
            return null;
        var nullTest = cursor;
        var test = body[cursor++];
        if (test.Code != Code.Test_rm64_r64 || test.Op0Kind != OpKind.Register ||
            test.Op1Kind != OpKind.Register || test.Op0Register != test.Op1Register ||
            !values.TryGetValue(test.Op0Register, out var array) || array.Kind != ValueKind.Entry)
            return null;
        var nullBranch = body[cursor++];
        if (!Branch(nullBranch, Mnemonic.Je) || !Setup() || cursor + 1 >= body.Count)
            return null;
        var boundsCompare = cursor;
        var compare = body[cursor++];
        if (compare.Code != Code.Cmp_r32_rm32 || compare.Op0Kind != OpKind.Register ||
            compare.Op0Register.GetSize() != 4 || compare.Op1Kind != OpKind.Memory ||
            !Memory(compare, test.Op0Register, Register.None, 1, lengthOffset, 4) ||
            !values.TryGetValue(test.Op0Register, out var checkedArray) || checkedArray != array ||
            !values.TryGetValue(compare.Op0Register.GetFullRegister(), out var index) ||
            index.Kind == ValueKind.Literal || index.Origin == array.Origin)
            return null;
        var boundsBranch = body[cursor++];
        if (!Branch(boundsBranch, Mnemonic.Jae) || !Setup() || cursor >= body.Count)
            return null;
        var storeIndex = cursor;
        var store = body[cursor++];
        if (store.Mnemonic != Mnemonic.Mov || store.OpCount != 2 ||
            store.Op0Kind != OpKind.Memory || store.MemorySize.GetSize() != 1 ||
            store.MemoryIndexScale != 1 || store.MemoryDisplacement64 != (ulong)dataOffset ||
            !values.TryGetValue(store.MemoryBase, out var first) ||
            !values.TryGetValue(store.MemoryIndex, out var second))
            return null;
        if (first.Kind == ValueKind.SignedIndex)
            (first, second) = (second, first);
        if (first != array || second.Kind != ValueKind.SignedIndex ||
            second.Origin != index.Origin)
            return null;
        Register valueEntry;
        int? literal;
        if (store.Op1Kind == OpKind.Immediate8 && store.Immediate8 is 0 or 1)
            (valueEntry, literal) = (Register.None, store.Immediate8);
        else if (store.Code == Code.Mov_rm8_r8 && store.Op1Kind == OpKind.Register &&
                 store.Op1Register.GetSize() == 1 && store.Op1Register is not
                     (Register.AH or Register.BH or Register.CH or Register.DH) &&
                 values.TryGetValue(store.Op1Register.GetFullRegister(), out var source) &&
                 source.Kind != ValueKind.SignedIndex)
            (valueEntry, literal) = source.Kind == ValueKind.Literal
                ? (Register.None, (int?)source.Literal) : (source.Origin, (int?)null);
        else
            return null;
        if (literal == null && (valueEntry == array.Origin || valueEntry == index.Origin) ||
            cursor + 4 >= body.Count || !X64Stack28BodyProof.Stack(body[cursor++], Mnemonic.Add))
            return null;
        var returnIndex = cursor;
        if (body[cursor++].Code != Code.Retnq)
            return null;
        var nullCall = cursor;
        if (!Call(body[cursor++]) || body[cursor++].Code != Code.Int3)
            return null;
        var boundsCall = cursor;
        if (!Call(body[cursor++]) || nullBranch.NearBranchTarget != body[nullCall].IP ||
            boundsBranch.NearBranchTarget != body[boundsCall].IP)
            return null;
        var complete = body.Take(cursor).ToArray();
        if (complete.Any(instruction => instruction.IsInvalid ||
            instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
            instruction.HasRepPrefix || instruction.HasRepnePrefix ||
            instruction.SegmentPrefix != Register.None) ||
            complete.Where((instruction, position) => position > 0 &&
                instruction.IP != complete[position - 1].NextIP).Any())
            return null;
        return new Shape(cursor, array.Origin, index.Origin, valueEntry, literal,
            nullTest, boundsCompare, storeIndex, returnIndex, nullCall, boundsCall);
    }

    private static bool TrySetup(Instruction instruction, Dictionary<Register, Value> values)
    {
        if (instruction.Op0Kind != OpKind.Register || !Volatile(instruction.Op0Register))
            return false;
        var destination = instruction.Op0Register.GetFullRegister();
        if (instruction.Code == Code.Movsxd_r64_rm32 && instruction.Op1Kind == OpKind.Register &&
            values.TryGetValue(instruction.Op1Register.GetFullRegister(), out var extended) &&
            extended.Kind is ValueKind.Entry or ValueKind.Low32 or ValueKind.SignedIndex)
        {
            values[destination] = extended with { Kind = ValueKind.SignedIndex };
            return true;
        }
        if (instruction.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64 or
            Code.Mov_r32_rm32 or Code.Mov_rm32_r32 &&
            instruction.Op1Kind == OpKind.Register &&
            values.TryGetValue(instruction.Op1Register.GetFullRegister(), out var copied) &&
            (instruction.Op0Register.GetSize() == 4 || copied.Kind == ValueKind.Entry))
        {
            values[destination] = copied with
                { Kind = instruction.Op0Register.GetSize() == 4 ? ValueKind.Low32 : copied.Kind };
            return true;
        }
        if (instruction.Code is Code.Xor_rm32_r32 or Code.Xor_r32_rm32 && instruction.Op1Kind == OpKind.Register &&
            instruction.Op0Register == instruction.Op1Register)
        {
            values[destination] = new Value(Register.None, ValueKind.Literal);
            return true;
        }
        return false;
    }

    private static bool Volatile(Register register) => register.GetFullRegister() is
        Register.RAX or Register.RCX or Register.RDX or Register.R8 or Register.R9 or
        Register.R10 or Register.R11;
    private static bool Branch(Instruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64;
    private static bool Call(Instruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
    private static bool Memory(Instruction instruction, Register @base, Register index,
        int scale, long offset, int size) => instruction.MemoryBase == @base &&
        instruction.MemoryIndex == index && instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == (ulong)offset && instruction.MemorySize.GetSize() == size;
}
