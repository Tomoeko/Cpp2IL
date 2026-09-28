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
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves the entire guarded read of one Boolean[] field at an Int32 argument.
/// The source field load, array null exit, unsigned bounds exit and byte-to-Boolean
/// predicate are all bound to the exact native body before emitting managed IL.
/// </summary>
internal static class X64FieldBooleanArrayReadProof
{
    internal sealed record Evidence(FieldAnalysisContext ArrayField);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe || method.IsStatic || method.IsVirtual ||
                method.Name is ".ctor" or ".cctor" ||
                method.Name != method.DefaultName || method.OverrideReturnType != null ||
                method.GenericParameters.Count != 0 ||
                method.Attributes != method.DefaultAttributes ||
                method.ImplAttributes != method.DefaultImplAttributes ||
                (method.Attributes & (MethodAttributes.Abstract |
                    MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) != 0 ||
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                    MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
                method.DeclaringType is not { Definition: { GenericContainer: null,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
                !NullCheckedCall.IsReferenceClass(owner) ||
                owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
                owner.GenericParameters.Count != 0 ||
                owner.Name != owner.DefaultName ||
                owner.Namespace != owner.DefaultNamespace ||
                owner.Attributes != owner.DefaultAttributes ||
                !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
                owner.Definition is not { PackingSizeIsDefault: true,
                    ClassSizeIsDefault: true } ||
                method.Definition is not { GenericContainer: null, parameterCount: 1,
                    InternalParameterData: [var rawParameter],
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
                !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
                method.Parameters is not [var parameter] ||
                parameter.ParameterIndex != 0 || parameter.IsRef ||
                !ReferenceEquals(parameter.DeclaringMethod, method) ||
                !ReferenceEquals(parameter.Definition, rawParameter) ||
                parameter.Attributes != parameter.DefaultAttributes ||
                parameter.OverrideParameterType != null ||
                !ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt32Type) ||
                rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
                !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType) ||
                !ReferenceEquals(method.DefaultReturnType, app.SystemTypes.SystemBooleanType) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var bindings) || bindings is not [var bound] ||
                !ReferenceEquals(bound, method) ||
                X64Stack28BodyProof.Read(method, 14, 64) is not { } body ||
                TryProveShape(body, pe) is not { } fieldOffset ||
                X86RuntimeNullThrowProof.TryIdentify(app, body[11].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app, body[13].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[11].IP, body[13].IP }) != null)
                return null;

            var candidates = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == fieldOffset).ToArray();
            if (candidates is not [{ } arrayField] ||
                arrayField.Name != arrayField.DefaultName ||
                arrayField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                arrayField.FieldType is not SzArrayTypeAnalysisContext
                    { ElementType: var element } ||
                !ReferenceEquals(element, app.SystemTypes.SystemBooleanType))
                return null;

            var receiver = new LocalVariable("proved-owner",
                new ManagedRegister(null, "rcx"), owner);
            var access = new FieldReference(arrayField, receiver, fieldOffset);
            return (NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access) ||
                    NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayoutWithFieldlessConstructedBase(access))
                ? new Evidence(arrayField) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static int? TryProveShape(IReadOnlyList<NativeInstruction> body, PE pe)
    {
        if (body.Count != 14 || !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            body[0].IP == 0 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !FieldLoad(body[1], out var fieldOffset) ||
            fieldOffset < 2 * pe.PointerSizeBytes || fieldOffset > 104 ||
            fieldOffset % pe.PointerSizeBytes != 0 ||
            !Registers(body[2], Code.Test_rm64_r64, NativeRegister.R8,
                NativeRegister.R8) ||
            !Branch(body[3], Code.Je_rel8_64, body[11].IP) ||
            !LengthCompare(body[4], pe) ||
            !Branch(body[5], Code.Jae_rel8_64, body[13].IP) ||
            !Registers(body[6], Code.Movsxd_r64_rm32, NativeRegister.RAX,
                NativeRegister.EDX) ||
            !ElementCompare(body[7], pe) ||
            body[8].Code != Code.Setne_rm8 ||
            body[8].Op0Kind != OpKind.Register ||
            body[8].Op0Register != NativeRegister.AL ||
            !X64Stack28BodyProof.Stack(body[9], Mnemonic.Add) ||
            body[10].Code != Code.Retnq || body[10].OpCount != 0 ||
            body[11].Code != Code.Call_rel32_64 ||
            body[11].Op0Kind != OpKind.NearBranch64 ||
            body[11].NearBranchTarget == 0 ||
            body[12].Code != Code.Int3 ||
            body[13].Code != Code.Call_rel32_64 ||
            body[13].Op0Kind != OpKind.NearBranch64 ||
            body[13].NearBranchTarget == 0)
            return null;

        return fieldOffset;
    }

    private static bool FieldLoad(NativeInstruction instruction, out int offset)
    {
        offset = 0;
        if (instruction.Code != Code.Mov_r64_rm64 ||
            instruction.Op0Kind != OpKind.Register ||
            instruction.Op0Register != NativeRegister.R8 ||
            !Memory(instruction, 1, NativeRegister.RCX, NativeRegister.None,
                1, instruction.MemoryDisplacement64, 8) ||
            instruction.MemoryDisplacement64 > int.MaxValue)
            return false;
        offset = (int)instruction.MemoryDisplacement64;
        return true;
    }

    private static bool LengthCompare(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Cmp_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EDX &&
        instruction.MemoryDisplacement64 <= uint.MaxValue &&
        Il2CppArrayUtils.IsIl2cppLengthAccessor(
            (uint)instruction.MemoryDisplacement64, pe) &&
        Memory(instruction, 1, NativeRegister.R8, NativeRegister.None,
            1, instruction.MemoryDisplacement64, 4);

    private static bool ElementCompare(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Cmp_rm8_imm8 &&
        Memory(instruction, 0, NativeRegister.RAX, NativeRegister.R8,
            1, Il2CppArrayUtils.GetFirstItemOffset(pe), 1) &&
        instruction.Op1Kind == OpKind.Immediate8 &&
        instruction.Immediate8 == 0;

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool Branch(NativeInstruction instruction, Code code, ulong target) =>
        instruction.Code == code &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool Memory(NativeInstruction instruction, int operand,
        NativeRegister @base, NativeRegister index, int scale,
        ulong displacement, int size) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == @base && instruction.MemoryIndex == index &&
        instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == displacement &&
        instruction.MemorySize.GetSize() == size;
}
