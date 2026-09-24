using System;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves one exact x64 null-guarded tail call that forwards an unchanged Int32-backed
/// enum parameter from RDX to the same managed parameter type on a field receiver.
/// </summary>
internal static class X64GuardedEnumParameterCallProof
{
    internal sealed record Evidence(MethodAnalysisContext Target, FieldAnalysisContext ReceiverField);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !OrdinaryVoidInstanceMethod(method) ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [var sourceParameterDefinition] ||
            method.Parameters is not [var sourceParameter] ||
            !UnchangedEnumParameter(method, sourceParameter, sourceParameterDefinition) ||
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
            !OrdinaryVoidInstanceMethod(target) ||
            !ReferenceEquals(target.DeclaringType, receiverType) ||
            !AccessibleTarget(owner, target) ||
            target.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } targetDefinition ||
            targetDefinition.InternalParameterData is not [var targetParameterDefinition] ||
            target.Parameters is not [var targetParameter] ||
            !ReferenceEquals(targetParameter.ParameterType, sourceParameter.ParameterType) ||
            !UnchangedEnumParameter(target, targetParameter, targetParameterDefinition) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target))
            return null;

        return new Evidence(target, receiverField);
    }

    private static bool OrdinaryVoidInstanceMethod(MethodAnalysisContext method) =>
        !method.IsStatic && !method.IsVirtual && method.IsVoid &&
        method.Name is not (".ctor" or ".cctor") && method.Name == method.DefaultName &&
        method.OverrideReturnType == null && method.GenericParameters.Count == 0 &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) == 0;

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

    private static bool UnchangedEnumParameter(MethodAnalysisContext method,
        ParameterAnalysisContext parameter, LibCpp2IL.Metadata.Il2CppParameterDefinition definition)
    {
        var enumType = parameter.ParameterType;
        if (parameter.ParameterIndex != 0 || !ReferenceEquals(parameter.DeclaringMethod, method) ||
            !ReferenceEquals(parameter.Definition, definition) || parameter.IsRef ||
            parameter.Attributes != parameter.DefaultAttributes || parameter.OverrideParameterType != null ||
            definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            enumType.Definition is not { IsEnumType: true, GenericContainer: null } ||
            enumType.IsGenericInstance || enumType.GenericParameters.Count != 0 ||
            enumType.Attributes != enumType.DefaultAttributes ||
            !ReferenceEquals(enumType.BaseType, enumType.DefaultBaseType) ||
            !ReferenceEquals(enumType.EnumUnderlyingType, method.AppContext.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(enumType.DefaultEnumUnderlyingType, enumType.EnumUnderlyingType))
            return false;

        var backing = enumType.Fields.Where(field => !field.IsStatic).ToArray();
        return backing is [{ } value] && value.Name == "value__" &&
               value.Name == value.DefaultName && value.Attributes == value.DefaultAttributes &&
               value.Offset == value.DefaultOffset && value.OverrideFieldType == null &&
               ReferenceEquals(value.FieldType, method.AppContext.SystemTypes.SystemInt32Type) &&
               value.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                   NumMods: 0, Byref: 0, Pinned: 0 };
    }

}
