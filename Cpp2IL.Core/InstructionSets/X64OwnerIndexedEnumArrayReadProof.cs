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
/// Proves a closed enum-array getter whose index is a second field on the same
/// receiver. The native array-null and unsigned bounds exits are supplied by
/// the managed element access only after both field bindings are established.
/// </summary>
internal static class X64OwnerIndexedEnumArrayReadProof
{
    internal sealed record Evidence(FieldAnalysisContext ArrayField,
        FieldAnalysisContext IndexField, TypeAnalysisContext ElementType);

    internal sealed record NativeShape(int ArrayOffset, int IndexOffset);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                !OrdinaryGetter(method) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var bindings) ||
                bindings is not [var bound] || !ReferenceEquals(bound, method) ||
                X64Stack28BodyProof.Read(method, 13, 64) is not { } body ||
                TryProveShape(body, pe) is not { } shape ||
                X86RuntimeNullThrowProof.TryIdentify(app,
                    body[10].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app,
                    body[12].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[10].IP, body[12].IP }) != null)
                return null;

            var owner = method.DeclaringType!;
            var arrayCandidates = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.ArrayOffset).ToArray();
            var indexCandidates = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.IndexOffset).ToArray();
            if (arrayCandidates is not [{ } arrayField] ||
                indexCandidates is not [{ } indexField] ||
                ReferenceEquals(arrayField, indexField) ||
                arrayField.Name != arrayField.DefaultName ||
                indexField.Name != indexField.DefaultName ||
                arrayField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                arrayField.FieldType is not SzArrayTypeAnalysisContext
                    { ElementType: var element } ||
                !ReferenceEquals(element, method.ReturnType) ||
                indexField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                !ReferenceEquals(indexField.FieldType,
                    app.SystemTypes.SystemInt32Type))
                return null;

            var receiver = new LocalVariable("proved-owner",
                new ManagedRegister(null, "rcx"), owner);
            return NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                       new FieldReference(arrayField, receiver,
                           shape.ArrayOffset)) &&
                   NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                       new FieldReference(indexField, receiver,
                           shape.IndexOffset), 32)
                ? new Evidence(arrayField, indexField, element) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool OrdinaryGetter(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (method.DeclaringType is not
                { Definition: { GenericContainer: null,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 ||
            owner.Name != owner.DefaultName ||
            owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            owner.Definition is not
                { PackingSizeIsDefault: true, ClassSizeIsDefault: true } ||
            method.IsStatic || method.IsVirtual || method.IsVoid ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 || method.Parameters.Count != 0 ||
            method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.SpecialName |
                                  MethodAttributes.Abstract |
                                  MethodAttributes.PinvokeImpl)) !=
                MethodAttributes.SpecialName ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            method.Definition is not
                { GenericContainer: null, parameterCount: 0,
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            method.ReturnType is not { } element ||
            !Enum32StorageProof.IsUnchanged(element) ||
            !ReferenceEquals(element.EnumUnderlyingType,
                app.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType))
            return false;

        var properties = owner.Properties.Where(property =>
            ReferenceEquals(property.Getter, method)).ToArray();
        return properties is [{ } property] && property.Setter == null &&
               property.Definition is { } rawProperty &&
               ReferenceEquals(rawProperty.Getter, definition) &&
               property.Name == property.DefaultName &&
               method.Name == "get_" + property.Name &&
               property.Attributes == property.DefaultAttributes &&
               property.OverridePropertyType == null && !property.IsStatic &&
               ReferenceEquals(property.PropertyType, element) &&
               rawProperty.RawPropertyType is
                   { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                       NumMods: 0, Byref: 0, Pinned: 0 };
    }

    internal static NativeShape? TryProveShape(IReadOnlyList<NativeInstruction> body,
        PE pe)
    {
        if (body.Count != 13 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !MemoryLoad(body[1], NativeRegister.RDX, NativeRegister.RCX,
                8, out var arrayOffset) ||
            !Registers(body[2], Code.Test_rm64_r64,
                NativeRegister.RDX, NativeRegister.RDX) ||
            !Branch(body[3], Code.Je_rel8_64, body[10].IP) ||
            !MemoryLoad(body[4], NativeRegister.RAX, NativeRegister.RCX,
                4, out var indexOffset, Code.Movsxd_r64_rm32) ||
            !LengthCompare(body[5], pe) ||
            !Branch(body[6], Code.Jae_rel8_64, body[12].IP) ||
            !ElementLoad(body[7], pe) ||
            !X64Stack28BodyProof.Stack(body[8], Mnemonic.Add) ||
            body[9].Code != Code.Retnq || body[9].OpCount != 0 ||
            body[10].Code != Code.Call_rel32_64 ||
            body[10].Op0Kind != OpKind.NearBranch64 ||
            body[11].Code != Code.Int3 ||
            body[12].Code != Code.Call_rel32_64 ||
            body[12].Op0Kind != OpKind.NearBranch64 ||
            arrayOffset < 16 || arrayOffset > 0x1000 - 8 ||
            indexOffset < 16 || indexOffset > 0x1000 - 4 ||
            NarrowFieldEqualityProof.StorageRangesOverlap(arrayOffset,
                pe.PointerSizeBytes, indexOffset, 4) ||
            arrayOffset % pe.PointerSizeBytes != 0 ||
            indexOffset % 4 != 0)
            return null;
        return new NativeShape(arrayOffset, indexOffset);
    }

    private static bool MemoryLoad(NativeInstruction instruction,
        NativeRegister result, NativeRegister source, int width, out int offset,
        Code code = Code.Mov_r64_rm64)
    {
        offset = 0;
        if (instruction.Code != code ||
            instruction.Op0Kind != OpKind.Register ||
            instruction.Op0Register != result ||
            instruction.Op1Kind != OpKind.Memory ||
            instruction.MemoryBase != source ||
            instruction.MemoryIndex != NativeRegister.None ||
            instruction.MemoryIndexScale != 1 ||
            instruction.MemorySize.GetSize() != width ||
            instruction.MemoryDisplacement64 > int.MaxValue)
            return false;
        offset = (int)instruction.MemoryDisplacement64;
        return true;
    }

    private static bool LengthCompare(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Cmp_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EAX &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RDX &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryIndexScale == 1 &&
        instruction.MemorySize.GetSize() == 4 &&
        instruction.MemoryDisplacement64 <= uint.MaxValue &&
        Il2CppArrayUtils.IsIl2cppLengthAccessor(
            (uint)instruction.MemoryDisplacement64, pe);

    private static bool ElementLoad(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Mov_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EAX &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RDX &&
        instruction.MemoryIndex == NativeRegister.RAX &&
        instruction.MemoryIndexScale == 4 &&
        instruction.MemorySize.GetSize() == 4 &&
        instruction.MemoryDisplacement64 ==
            Il2CppArrayUtils.GetFirstItemOffset(pe);

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister first, NativeRegister second) =>
        instruction.Code == code &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == first &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == second;

    private static bool Branch(NativeInstruction instruction, Code code,
        ulong target) =>
        instruction.Code == code &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;
}
