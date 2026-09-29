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
/// Proves a complete reference-array getter indexed by an Int32 field plus one.
/// The two native failure exits are replaced by the managed array access only
/// after the field layout, complete native path, and helper effects are proved.
/// The recovered IL reads the ordinary Int32 field before its implicit array
/// null check; stripped metadata cannot establish original volatile intent.
/// </summary>
internal static class X64FieldPlusOneReferenceArrayReadProof
{
    internal sealed record Evidence(FieldAnalysisContext ArrayField,
        FieldAnalysisContext IndexField);

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
                X64Stack28BodyProof.Read(method, 14, 80) is not { } body ||
                TryProveShape(body, pe) is not { } shape ||
                X86RuntimeNullThrowProof.TryIdentify(app,
                    body[11].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app,
                    body[13].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[11].IP, body[13].IP }) != null)
                return null;

            var owner = method.DeclaringType!;
            var arrays = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.ArrayOffset).ToArray();
            var indices = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.IndexOffset).ToArray();
            if (arrays is not [{ } arrayField] ||
                indices is not [{ } indexField] ||
                ReferenceEquals(arrayField, indexField) ||
                arrayField.Name != arrayField.DefaultName ||
                indexField.Name != indexField.DefaultName ||
                arrayField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                        NumMods: 0, Byref: 0, Pinned: 0 } rawArray ||
                arrayField.FieldType is not SzArrayTypeAnalysisContext
                    { ElementType: var element } ||
                !ReferenceEquals(element, method.ReturnType) ||
                rawArray.GetEncapsulatedType() is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } rawElement ||
                !ReferenceEquals(rawElement.AsClass(),
                    method.ReturnType.Definition) ||
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
                ? new Evidence(arrayField, indexField) : null;
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
                        NumMods: 0, Byref: 0, Pinned: 0 },
                    PackingSizeIsDefault: true, ClassSizeIsDefault: true } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 ||
            owner.Name != owner.DefaultName ||
            owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
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
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            method.ReturnType is not
                { Definition: { GenericContainer: null,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } } } result ||
            result.IsValueType || result.IsInterface || result.IsGenericInstance ||
            result.GenericParameters.Count != 0 ||
            result.Name != result.DefaultName ||
            result.Namespace != result.DefaultNamespace ||
            result.Attributes != result.DefaultAttributes ||
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
               ReferenceEquals(property.PropertyType, result) &&
               rawProperty.RawPropertyType is
                   { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                       NumMods: 0, Byref: 0, Pinned: 0 };
    }

    internal static NativeShape? TryProveShape(IReadOnlyList<NativeInstruction> body,
        PE pe)
    {
        if (body.Count != 14 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !MemoryLoad(body[1], NativeRegister.RDX, NativeRegister.RCX,
                8, Code.Mov_r64_rm64, out var arrayOffset) ||
            !Registers(body[2], Code.Test_rm64_r64,
                NativeRegister.RDX, NativeRegister.RDX) ||
            !Branch(body[3], Code.Je_rel8_64, body[11].IP) ||
            !MemoryLoad(body[4], NativeRegister.RAX, NativeRegister.RCX,
                4, Code.Movsxd_r64_rm32, out var indexOffset) ||
            body[5].Code != Code.Inc_rm64 || body[5].OpCount != 1 ||
            body[5].Op0Kind != OpKind.Register ||
            body[5].Op0Register != NativeRegister.RAX ||
            !LengthCompare(body[6], pe) ||
            !Branch(body[7], Code.Jae_rel8_64, body[13].IP) ||
            !ElementLoad(body[8], pe) ||
            !X64Stack28BodyProof.Stack(body[9], Mnemonic.Add) ||
            body[10].Code != Code.Retnq || body[10].OpCount != 0 ||
            !DirectCall(body[11]) || body[12].Code != Code.Int3 ||
            !DirectCall(body[13]) ||
            arrayOffset < 16 || arrayOffset > 0x1000 - 8 ||
            indexOffset < 16 || indexOffset > 0x1000 - 4 ||
            arrayOffset % pe.PointerSizeBytes != 0 || indexOffset % 4 != 0 ||
            NarrowFieldEqualityProof.StorageRangesOverlap(arrayOffset,
                pe.PointerSizeBytes, indexOffset, 4))
            return null;
        return new NativeShape(arrayOffset, indexOffset);
    }

    private static bool MemoryLoad(NativeInstruction instruction,
        NativeRegister result, NativeRegister source, int width, Code code,
        out int offset)
    {
        offset = 0;
        if (instruction.Code != code || instruction.Op0Kind != OpKind.Register ||
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
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RAX &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RDX &&
        instruction.MemoryIndex == NativeRegister.RAX &&
        instruction.MemoryIndexScale == pe.PointerSizeBytes &&
        instruction.MemorySize.GetSize() == pe.PointerSizeBytes &&
        instruction.MemoryDisplacement64 ==
            Il2CppArrayUtils.GetFirstItemOffset(pe);

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister first, NativeRegister second) =>
        instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == first &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == second;

    private static bool Branch(NativeInstruction instruction, Code code,
        ulong target) =>
        instruction.Code == code && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
}
