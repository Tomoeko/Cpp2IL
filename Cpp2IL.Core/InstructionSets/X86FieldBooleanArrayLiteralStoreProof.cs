using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete instance-field Boolean[] literal store with null and bounds exits.
/// The only successful effect is the byte store through the field's array reference.
/// </summary>
internal static class X86FieldBooleanArrayLiteralStoreProof
{
    internal sealed record Evidence(FieldAnalysisContext ArrayField, bool Value);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native)
    {
        if (Find(method, native) is not { } evidence)
            return null;

        var array = new ManagedRegister(null, "field_boolean_array");
        var field = new ISIL.MemoryOperand(new ManagedRegister(null, "rcx"), null,
            evidence.ArrayField.Offset);
        var element = new ISIL.MemoryOperand(array, new ManagedRegister(null, "rdx"),
            (int)Il2CppArrayUtils.GetFirstItemOffset(method.AppContext.Binary), 1);
        return
        [
            new(0, ISIL.OpCode.Move, array, field),
            new(1, ISIL.OpCode.Move, element, new Immediate(evidence.Value ? 1 : 0)),
            new(2, ISIL.OpCode.Return),
        ];
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native)
    {
        var evidence = FindBoundMethod(method, native);
        if (evidence == null ||
            !method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                out var aliases) ||
            aliases.Count(candidate => ReferenceEquals(candidate, method)) != 1)
            return null;
        if (aliases.Count == 1)
            return evidence;

        // Identical code folding may bind two distinct managed owners to the
        // same native body. Require both owner-specific field layouts and both
        // signatures to prove this exact Boolean store independently.
        if (aliases is not [var first, var second] ||
            ReferenceEquals(first, second))
            return null;
        var other = ReferenceEquals(first, method) ? second : first;
        return other.UnderlyingPointer == method.UnderlyingPointer &&
               !ReferenceEquals(other.DeclaringType, method.DeclaringType) &&
               FindBoundMethod(other, native) != null
            ? evidence : null;
    }

    private static Evidence? FindBoundMethod(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
            method.DeclaringType is not { Definition: { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 || owner.Attributes != owner.DefaultAttributes ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            method.Definition is not { GenericContainer: null,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 }, parameterCount: 1,
                InternalParameterData: [var rawParameter] } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 || method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            method.Parameters is not [var index] || index.ParameterIndex != 0 ||
            !ReferenceEquals(index.DeclaringMethod, method) ||
            !ReferenceEquals(index.Definition, rawParameter) || index.IsRef ||
            index.Attributes != index.DefaultAttributes ||
            index.OverrideParameterType != null ||
            !ReferenceEquals(index.ParameterType, app.SystemTypes.SystemInt32Type) ||
            rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            TryProveShape(native, pe) is not { } shape ||
            native[0].IP != method.UnderlyingPointer)
            return null;

        var candidates = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.FieldOffset).ToArray();
        if (candidates is not [{ } matched] || matched.Name != matched.DefaultName ||
            matched.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                    NumMods: 0, Byref: 0, Pinned: 0 } rawArray ||
            rawArray.GetEncapsulatedType() is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            matched.FieldType is not SzArrayTypeAnalysisContext { ElementType: var element } ||
            !ReferenceEquals(element, app.SystemTypes.SystemBooleanType) ||
            !HasProvedFieldLayout(matched, owner, shape.FieldOffset))
            return null;

        var body = X64Stack28BodyProof.Read(method, 13, 96);
        if (body == null || !body.SequenceEqual(native.Take(13)) ||
            TryProveShape(body, pe) != shape ||
            X86RuntimeNullThrowProof.TryIdentify(app, body[10].NearBranchTarget) == null ||
            !X86RuntimeBoundsThrowProof.TryIdentify(app, body[12].NearBranchTarget) ||
            X86CallerExceptionRegionProof.Check(method, body,
                new HashSet<ulong> { body[10].IP, body[12].IP }) != null)
            return null;

        return new Evidence(matched, shape.Value);
    }

    private static bool HasProvedFieldLayout(FieldAnalysisContext field,
        TypeAnalysisContext owner, int offset)
    {
        var access = new FieldReference(field,
            new LocalVariable("proved-owner", new ManagedRegister(null, "rcx"), owner), offset);
        return NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access) ||
               NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayoutWithFieldlessConstructedBase(access);
    }

    internal sealed record Shape(int FieldOffset, bool Value);

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body, PE pe)
    {
        if (body.Count < 13 || !FieldLoad(body[1], out var fieldOffset))
            return null;
        for (var index = 0; index < 13; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None ||
                index > 0 && instruction.IP != body[index - 1].NextIP)
                return null;
        }

        if (!X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !Registers(body[2], Code.Test_rm64_r64, NativeRegister.R8, NativeRegister.R8) ||
            !Branch(body[3], Code.Je_rel8_64, body[10].IP) ||
            body[4].Code != Code.Cmp_r32_rm32 ||
            body[4].Op0Kind != OpKind.Register || body[4].Op0Register != NativeRegister.EDX ||
            body[4].MemoryDisplacement64 > uint.MaxValue ||
            !Il2CppArrayUtils.IsIl2cppLengthAccessor(
                (uint)body[4].MemoryDisplacement64, pe) ||
            !Memory(body[4], 1, NativeRegister.R8, NativeRegister.None, 1,
                body[4].MemoryDisplacement64, 4) ||
            !Branch(body[5], Code.Jae_rel8_64, body[12].IP) ||
            !Registers(body[6], Code.Movsxd_r64_rm32, NativeRegister.RAX, NativeRegister.EDX) ||
            body[7].Code != Code.Mov_rm8_imm8 ||
            !Memory(body[7], 0, NativeRegister.RAX, NativeRegister.R8, 1,
                Il2CppArrayUtils.GetFirstItemOffset(pe), 1) ||
            body[7].Op1Kind != OpKind.Immediate8 || body[7].Immediate8 is not (0 or 1) ||
            !X64Stack28BodyProof.Stack(body[8], Mnemonic.Add) ||
            body[9].Code != Code.Retnq || body[9].OpCount != 0 ||
            !Call(body[10]) || body[11].Code != Code.Int3 || !Call(body[12]))
            return null;

        return new Shape(fieldOffset, body[7].Immediate8 == 1);
    }

    private static bool FieldLoad(NativeInstruction instruction, out int offset)
    {
        offset = 0;
        if (instruction.Code != Code.Mov_r64_rm64 ||
            instruction.Op0Kind != OpKind.Register || instruction.Op0Register != NativeRegister.R8 ||
            instruction.MemoryDisplacement64 is < 16 or > 0xFF8 ||
            !Memory(instruction, 1, NativeRegister.RCX, NativeRegister.None, 1,
                instruction.MemoryDisplacement64, 8))
            return false;
        offset = (int)instruction.MemoryDisplacement64;
        return true;
    }

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool Branch(NativeInstruction instruction, Code code, ulong target) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool Memory(NativeInstruction instruction, int operand,
        NativeRegister @base, NativeRegister index, int scale, ulong offset, int size) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == @base && instruction.MemoryIndex == index &&
        instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == offset && instruction.MemorySize.GetSize() == size;

    private static bool Call(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
}
