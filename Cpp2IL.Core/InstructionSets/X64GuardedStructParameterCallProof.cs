using System;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves the by-value eight-byte struct ABI for one guarded field-receiver tail
/// call. The second, reference-typed caller argument is overwritten in R8 and
/// cannot have an effect on the bound one-argument target.
/// </summary>
internal static class X64GuardedStructParameterCallProof
{
    internal sealed record Evidence(MethodAnalysisContext Target, FieldAnalysisContext ReceiverField);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !OrdinaryBoolInstanceMethod(method) ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 2,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [var sourceValueDefinition, var ignoredDefinition] ||
            method.Parameters is not [var sourceValue, var ignored] ||
            !UnchangedPairParameter(method, sourceValue, sourceValueDefinition) ||
            !UnchangedIgnoredReference(method, ignored, ignoredDefinition) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            X64GuardedParameterTailCallBodyProof.Find(method) is not { } shape)
            return null;

        var fields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == (long)shape.ReceiverOffset &&
            field.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (fields is not [{ } receiverField] ||
            receiverField.Name != receiverField.DefaultName ||
            receiverField.FieldType is not { Definition: { GenericContainer: null } } receiverType ||
            !NullCheckedCall.IsReferenceClass(receiverType))
            return null;
        var ownerLocal = new LocalVariable("proved-owner", new ManagedRegister(null, "proved-owner"), owner);
        if (!NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(receiverField, ownerLocal, (int)shape.ReceiverOffset)))
            return null;

        if (!app.MethodsByAddress.TryGetValue(shape.TargetAddress, out var bindings) ||
            bindings is not [var target] ||
            !OrdinaryBoolInstanceMethod(target) ||
            !ReferenceEquals(target.DeclaringType, receiverType) ||
            !AccessibleTarget(owner, target) ||
            target.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } } targetDefinition ||
            targetDefinition.InternalParameterData is not [var targetValueDefinition] ||
            target.Parameters is not [var targetValue] ||
            !ReferenceEquals(targetValue.ParameterType, sourceValue.ParameterType) ||
            !UnchangedPairParameter(target, targetValue, targetValueDefinition) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target))
            return null;

        return new Evidence(target, receiverField);
    }

    private static bool OrdinaryBoolInstanceMethod(MethodAnalysisContext method) =>
        !method.IsStatic && !method.IsVirtual &&
        ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemBooleanType) &&
        method.Name is not (".ctor" or ".cctor") && method.Name == method.DefaultName &&
        method.OverrideReturnType == null && method.GenericParameters.Count == 0 &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) == 0 &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method);

    private static bool UnchangedPairParameter(MethodAnalysisContext method,
        ParameterAnalysisContext parameter, LibCpp2IL.Metadata.Il2CppParameterDefinition definition)
    {
        var valueType = parameter.ParameterType;
        if (parameter.ParameterIndex != 0 || !ReferenceEquals(parameter.DeclaringMethod, method) ||
            !ReferenceEquals(parameter.Definition, definition) || parameter.IsRef ||
            parameter.Name != parameter.DefaultName ||
            parameter.Attributes != parameter.DefaultAttributes || parameter.OverrideParameterType != null ||
            parameter.OverrideAttributes != null || parameter.UseOverrideDefaultValue ||
            definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            valueType.Definition is not { GenericContainer: null, HasCctor: false,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true } ||
            !valueType.IsValueType || valueType.IsEnumType || valueType.IsGenericInstance ||
            valueType.GenericParameters.Count != 0 ||
            valueType.Name != valueType.DefaultName || valueType.Namespace != valueType.DefaultNamespace ||
            valueType.Attributes != valueType.DefaultAttributes ||
            (valueType.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout ||
            !ReferenceEquals(valueType.BaseType, valueType.DefaultBaseType) ||
            TypeSizes.UnboxedSize(valueType, 8) != 8 || valueType.Fields.Count != 2)
            return false;

        var fields = valueType.Fields.OrderBy(field => field.Offset).ToArray();
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.IsStatic || field.Offset != index * 4 || field.Offset != field.DefaultOffset ||
                field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes ||
                field.OverrideFieldType != null || field.UseOverrideConstantValue ||
                !ReferenceEquals(field.FieldType, method.AppContext.SystemTypes.SystemSingleType) ||
                field.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_R4, NumMods: 0, Byref: 0, Pinned: 0 })
                return false;
        }
        return true;
    }

    private static bool UnchangedIgnoredReference(MethodAnalysisContext method,
        ParameterAnalysisContext parameter, LibCpp2IL.Metadata.Il2CppParameterDefinition definition) =>
        parameter.ParameterIndex == 1 && ReferenceEquals(parameter.DeclaringMethod, method) &&
        ReferenceEquals(parameter.Definition, definition) && !parameter.IsRef &&
        parameter.Name == parameter.DefaultName &&
        parameter.Attributes == parameter.DefaultAttributes && parameter.OverrideParameterType == null &&
        parameter.OverrideAttributes == null && !parameter.UseOverrideDefaultValue &&
        definition.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
            NumMods: 0, Byref: 0, Pinned: 0 } &&
        parameter.ParameterType is { Definition: { GenericContainer: null } } ignoredType &&
        NullCheckedCall.IsReferenceClass(ignoredType);

    private static bool AccessibleTarget(TypeAnalysisContext caller, MethodAnalysisContext target)
    {
        var type = target.DeclaringType;
        if (type == null)
            return false;
        if (ReferenceEquals(type, caller))
            return true;
        var sameAssembly = ReferenceEquals(caller.DeclaringAssembly, type.DeclaringAssembly);
        var methodAccess = target.Attributes & MethodAttributes.MemberAccessMask;
        if (methodAccess != MethodAttributes.Public &&
            !(sameAssembly && (methodAccess is MethodAttributes.Assembly or MethodAttributes.FamORAssem)))
            return false;
        for (var current = type; current != null; current = current.DeclaringType)
        {
            var visibility = current.Attributes & TypeAttributes.VisibilityMask;
            if (current.DeclaringType == null)
            {
                if (visibility != TypeAttributes.Public &&
                    !(sameAssembly && visibility == TypeAttributes.NotPublic))
                    return false;
            }
            else if (visibility != TypeAttributes.NestedPublic &&
                     !(sameAssembly && (visibility is TypeAttributes.NestedAssembly or
                         TypeAttributes.NestedFamORAssem)))
                return false;
        }
        return true;
    }
}
