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
/// Proves a closed class-array element read followed by an Int32 or Single
/// field read. The native unsigned bounds exit and both null exits must have
/// the same order and effects as the emitted managed array/field reads.
/// </summary>
internal static class X64ArrayElementScalarFieldReadProof
{
    internal sealed record Evidence(FieldAnalysisContext ArrayField,
        FieldAnalysisContext ValueField);

    internal sealed record Shape(int ArrayOffset, int ValueOffset,
        Il2CppTypeEnum ValueType);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                method.UnderlyingPointer is 0 or ulong.MaxValue ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var bindings) || bindings.Count is < 1 or > 8 ||
                bindings.Count(binding => ReferenceEquals(binding, method)) != 1 ||
                X64Stack28BodyProof.Read(method, 16, 80) is not { } body ||
                TryProveShape(body, pe) is not { } shape ||
                X86RuntimeNullThrowProof.TryIdentify(app,
                    body[13].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app,
                    body[15].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[13].IP, body[15].IP }) != null)
                return null;

            Evidence? selected = null;
            foreach (var binding in bindings)
            {
                if (binding.UnderlyingPointer != method.UnderlyingPointer ||
                    !ReferenceEquals(binding.DeclaringType?.DeclaringAssembly,
                        method.DeclaringType?.DeclaringAssembly) ||
                    (!ReferenceEquals(binding, method) &&
                     (X64Stack28BodyProof.Read(binding, 16, 80) is not
                          { } aliasBody || !aliasBody.SequenceEqual(body))) ||
                    Bind(binding, shape) is not { } evidence)
                    return null;
                if (ReferenceEquals(binding, method))
                    selected = evidence;
            }
            return selected;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static Evidence? Bind(MethodAnalysisContext method, Shape shape)
    {
        if (!OrdinaryMethod(method, shape.ValueType))
            return null;
        var owner = method.DeclaringType!;
        var arrayFields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.ArrayOffset).ToArray();
        if (arrayFields is not [{ } arrayField] ||
            arrayField.Name != arrayField.DefaultName ||
            arrayField.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            arrayField.FieldType is not SzArrayTypeAnalysisContext
                { ElementType: var element } ||
            !OrdinaryClass(element) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(arrayField,
                    new LocalVariable("proved-owner",
                        new ManagedRegister(null, "rcx"), owner),
                    shape.ArrayOffset)))
            return null;

        var values = element.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.ValueOffset).ToArray();
        if (values is not [{ } valueField] ||
            valueField.Name != valueField.DefaultName ||
            valueField.BackingData?.Field.RawFieldType is not
                { NumMods: 0, Byref: 0, Pinned: 0 } rawValue ||
            rawValue.Type != shape.ValueType ||
            !ReferenceEquals(valueField.FieldType, method.ReturnType))
            return null;
        var reference = new FieldReference(valueField,
            new LocalVariable("proved-element",
                new ManagedRegister(null, "rcx"), element),
            shape.ValueOffset);
        return (shape.ValueType == Il2CppTypeEnum.IL2CPP_TYPE_R4
                ? NarrowFieldEqualityProof.HasUnchangedSingleFieldLayout(reference)
                : NarrowFieldEqualityProof.HasUnchangedFieldLayout(reference, 32))
            ? new Evidence(arrayField, valueField) : null;
    }

    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        type.Definition is { GenericContainer: null,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 },
            PackingSizeIsDefault: true, ClassSizeIsDefault: true } &&
        NullCheckedCall.IsReferenceClass(type) &&
        type.Name == type.DefaultName &&
        type.Namespace == type.DefaultNamespace &&
        type.Attributes == type.DefaultAttributes &&
        ReferenceEquals(type.BaseType, type.DefaultBaseType) &&
        type.GenericParameters.Count == 0 &&
        !type.IsValueType && !type.IsInterface && !type.IsGenericInstance;

    private static bool OrdinaryMethod(MethodAnalysisContext method,
        Il2CppTypeEnum valueType)
    {
        var app = method.AppContext;
        if (method.DeclaringType is not { } owner || !OrdinaryClass(owner) ||
            method.IsStatic || method.IsVirtual || method.IsVoid ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract |
                MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            method.Definition is not { GenericContainer: null,
                parameterCount: 1,
                RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } rawReturn } definition ||
            rawReturn.Type != valueType ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [{ } rawParameter] ||
            rawParameter.RawType is not
                { NumMods: 0, Byref: 0, Pinned: 0 } rawIndex ||
            method.Parameters is not [{ } parameter] ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            !ReferenceEquals(parameter.DeclaringMethod, method) ||
            parameter.ParameterIndex != 0 || parameter.IsRef ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideParameterType != null ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            !ReferenceEquals(method.ReturnType, valueType ==
                Il2CppTypeEnum.IL2CPP_TYPE_R4
                    ? app.SystemTypes.SystemSingleType
                    : app.SystemTypes.SystemInt32Type) ||
            owner.Properties.Any(property =>
                ReferenceEquals(property.Getter, method) ||
                ReferenceEquals(property.Setter, method)))
            return false;

        if (valueType == Il2CppTypeEnum.IL2CPP_TYPE_I4)
            return rawIndex.Type == Il2CppTypeEnum.IL2CPP_TYPE_I4 &&
                   ReferenceEquals(parameter.ParameterType,
                       app.SystemTypes.SystemInt32Type);
        var indexType = parameter.ParameterType;
        return rawIndex.Type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE &&
               indexType.Definition is { GenericContainer: null,
                   RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                       NumMods: 0, Byref: 0, Pinned: 0 } } &&
               indexType.IsEnumType && !indexType.IsGenericInstance &&
               indexType.GenericParameters.Count == 0 &&
               indexType.OverrideEnumUnderlyingType == null &&
               ReferenceEquals(indexType.EnumUnderlyingType,
                   app.SystemTypes.SystemInt32Type) &&
               indexType.Name == indexType.DefaultName &&
               indexType.Namespace == indexType.DefaultNamespace &&
               indexType.Attributes == indexType.DefaultAttributes;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body,
        PE pe)
    {
        if (body.Count != 16 || pe.PointerSizeBytes != 8 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !MemoryLoad(body[1], Code.Mov_r64_rm64, NativeRegister.R8,
                NativeRegister.RCX, 8, out var arrayOffset) ||
            !Registers(body[2], Code.Test_rm64_r64,
                NativeRegister.R8, NativeRegister.R8) ||
            !Branch(body[3], Code.Je_rel8_64, body[13].IP) ||
            !ArrayLengthCompare(body[4], pe) ||
            !Branch(body[5], Code.Jae_rel8_64, body[15].IP) ||
            !Registers(body[6], Code.Movsxd_r64_rm32,
                NativeRegister.RAX, NativeRegister.EDX) ||
            !ArrayElementLoad(body[7], pe, out var elementRegister) ||
            !Registers(body[8], Code.Test_rm64_r64,
                elementRegister, elementRegister) ||
            !Branch(body[9], Code.Je_rel8_64, body[13].IP) ||
            !X64Stack28BodyProof.Stack(body[11], Mnemonic.Add) ||
            body[12].Code != Code.Retnq || body[12].OpCount != 0 ||
            body[13].Code != Code.Call_rel32_64 ||
            body[13].Op0Kind != OpKind.NearBranch64 ||
            body[14].Code != Code.Int3 ||
            body[15].Code != Code.Call_rel32_64 ||
            body[15].Op0Kind != OpKind.NearBranch64 ||
            arrayOffset is < 16 or > 0x1000 - 8 || arrayOffset % 8 != 0)
            return null;

        var valueType = body[10].Code switch
        {
            Code.Mov_r32_rm32 => Il2CppTypeEnum.IL2CPP_TYPE_I4,
            Code.Movss_xmm_xmmm32 => Il2CppTypeEnum.IL2CPP_TYPE_R4,
            _ => Il2CppTypeEnum.IL2CPP_TYPE_END
        };
        if (valueType == Il2CppTypeEnum.IL2CPP_TYPE_END ||
            !MemoryLoad(body[10], body[10].Code,
                valueType == Il2CppTypeEnum.IL2CPP_TYPE_R4
                    ? NativeRegister.XMM0 : NativeRegister.EAX,
                elementRegister, 4, out var valueOffset) ||
            valueOffset is < 16 or > 0x1000 - 4 || valueOffset % 4 != 0)
            return null;
        return new Shape(arrayOffset, valueOffset, valueType);
    }

    private static bool MemoryLoad(NativeInstruction instruction, Code code,
        NativeRegister result, NativeRegister source, int width, out int offset)
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

    private static bool ArrayLengthCompare(NativeInstruction instruction,
        PE pe) =>
        instruction.Code == Code.Cmp_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EDX &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.R8 &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryIndexScale == 1 &&
        instruction.MemorySize.GetSize() == 4 &&
        instruction.MemoryDisplacement64 <= uint.MaxValue &&
        Il2CppArrayUtils.IsIl2cppLengthAccessor(
            (uint)instruction.MemoryDisplacement64, pe);

    private static bool ArrayElementLoad(NativeInstruction instruction,
        PE pe, out NativeRegister elementRegister)
    {
        elementRegister = instruction.Op0Register;
        return instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register &&
        elementRegister is NativeRegister.RCX or NativeRegister.RAX &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.R8 &&
        instruction.MemoryIndex == NativeRegister.RAX &&
        instruction.MemoryIndexScale == 8 &&
        instruction.MemorySize.GetSize() == 8 &&
        instruction.MemoryDisplacement64 ==
            Il2CppArrayUtils.GetFirstItemOffset(pe);
    }

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
