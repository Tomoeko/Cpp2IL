using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Checks original ordinary class declarations and retained external initialization dependencies.</summary>
internal static partial class X64OriginalReferenceClassProof
{
    internal static bool ValidTypeIndex(ApplicationAnalysisContext app, int index) =>
        index >= 0 && index < app.Binary.AllTypes.Length;

    internal static bool CoherentDescriptor(Il2CppType raw) => raw.Data != null &&
        raw.Datapoint == raw.Data.Dummy && raw.Attrs == (raw.Bits & 0xFFFF) &&
        raw.Type == (Il2CppTypeEnum)((raw.Bits >> 16) & 0xFF) &&
        raw.NumMods == ((raw.Bits >> 24) & 0x1F) && raw.Byref == ((raw.Bits >> 29) & 1) &&
        raw.Pinned == ((raw.Bits >> 30) & 1) && raw.ValueType == (raw.Bits >> 31);

    internal static bool ReferenceDescriptor(Il2CppType raw) => CoherentDescriptor(raw) &&
        raw.NumMods == 0 && raw.Byref == 0 && raw.Pinned == 0 && raw.ValueType == 0;

    internal static TypeAnalysisContext? ResolveClass(ApplicationAnalysisContext app, Il2CppType? raw)
    {
        if (raw == null || !ReferenceDescriptor(raw) || !OriginalDescriptor(app, raw)) return null;
        if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT)
        {
            var systemObject = app.SystemTypes.SystemObjectType;
            var definition = systemObject.Definition;
            return definition != null && ValidTypeIndex(app, definition.ByvalTypeIndex.Value) &&
                   ReferenceDescriptor(definition.RawType) && definition.RawType.Type == raw.Type &&
                   definition.TypeIndex.Value >= 0 && raw.Data.Dummy == (ulong)definition.TypeIndex.Value &&
                   definition.RawType.Data.Dummy == raw.Data.Dummy ? systemObject : null;
        }
        if (raw.Type != Il2CppTypeEnum.IL2CPP_TYPE_CLASS || raw.Data.Dummy >= (ulong)app.Metadata.TypeDefinitionCount)
            return null;
        var original = app.Metadata.typeDefs[(int)raw.Data.Dummy];
        if (original.DeclaringAssembly is not { } image || app.ResolveContextForAssembly(image) is not { } assembly ||
            !ReferenceEquals(assembly.Definition?.Image, image) || assembly.GetTypeByDefinition(original) is not { } type ||
            !ReferenceEquals(type.Definition, original) || !ReferenceEquals(type.AppContext, app) ||
            !ReferenceEquals(type.DeclaringAssembly, assembly) ||
            assembly.Types.Count(candidate => ReferenceEquals(candidate, type)) != 1)
            return null;
        return type;
    }

    internal static bool IsValid(TypeAnalysisContext type, bool retainAncestorInitializers)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null;)
        {
            if (!visited.Add(current) || visited.Count > 32 || current.Definition is not
                {
                    GenericContainerIndex: { IsNull: true }, DeclaringTypeIndex: { IsNull: true },
                    PackingSizeIsDefault: true, ClassSizeIsDefault: true
                } definition ||
                !OriginalType(type.AppContext, definition) ||
                !ValidTypeIndex(type.AppContext, definition.ByvalTypeIndex.Value) ||
                !definition.ParentIndex.IsNull && !ValidTypeIndex(type.AppContext, definition.ParentIndex.Value) ||
                !ReferenceEquals(ResolveClass(type.AppContext, definition.RawType), current) ||
                current.DeclaringType != null || current.IsValueType || current.IsInterface || current.IsGenericInstance ||
                current.GenericParameters.Count != 0 || current.Name != current.DefaultName ||
                current.Namespace != current.DefaultNamespace || current.Attributes != current.DefaultAttributes ||
                current.OverrideBaseType != null || (current.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
                !CanonicalAssembly(current.DeclaringAssembly) ||
                !HasLifecycleEligibility(type, current, retainAncestorInitializers) || !ConstantMembers(current, current.Definition!.HasCctor))
                return false;
            if (ReferenceEquals(current, type.AppContext.SystemTypes.SystemObjectType))
                return definition.ParentIndex.IsNull && definition.RawBaseType == null;
            if (ResolveClass(type.AppContext, definition.RawBaseType) is not { } parent ||
                !ReferenceEquals(current.BaseType, parent)) return false;
            current = parent;
        }
        return false;
    }

    private static bool ConstantMembers(TypeAnalysisContext type, bool retainCctor)
    {
        var definition = type.Definition!;
        if (!(definition.Methods ?? []).SequenceEqual(type.Methods.Select(method => method.Definition)) ||
            !(definition.Fields ?? []).SequenceEqual(type.Fields.Select(field => field.BackingData?.Field)) ||
            !(definition.Properties ?? []).SequenceEqual(type.Properties.Select(property => property.Definition)) ||
            !(definition.Events ?? []).SequenceEqual(type.Events.Select(member => member.Definition)) ||
            type.Methods.Any(method => method.Name == ".cctor" && (!retainCctor || !StaticConstructor(method)) || method.Name != method.DefaultName ||
                method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
                method.Name == ".ctor" && !InstanceConstructor(method) ||
                method.OverrideReturnType != null || !ReferenceEquals(method.DeclaringType, type) ||
                method.Definition == null || !OriginalMethod(type.AppContext, method.Definition) ||
                !ValidTypeIndex(type.AppContext, method.Definition.returnTypeIdx.Value) ||
                method.Definition.RawReturnType is not { } raw || !RetainedDescriptor(type.AppContext, raw) ||
                method.Parameters.Count != method.Definition.parameterCount ||
                method.Definition.InternalParameterData is not { } originals || originals.Length != method.Parameters.Count ||
                method.Parameters.Where((parameter, index) => !ReferenceEquals(parameter.Definition, originals[index]) ||
                    !OriginalParameter(type.AppContext, method.Definition, index, originals[index]) ||
                    !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.ParameterIndex != index ||
                    parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
                    parameter.OverrideParameterType != null || parameter.UseOverrideDefaultValue || parameter.Definition == null ||
                    !ValidTypeIndex(type.AppContext, parameter.Definition.typeIndex.Value) ||
                    parameter.Definition.RawType is not { } parameterRaw ||
                    !RetainedDescriptor(type.AppContext, parameterRaw)).Any()))
            return false;
        for (var ordinal = 0; ordinal < type.Properties.Count; ordinal++)
        {
            var property = type.Properties[ordinal];
            if (property.Definition is not { } original || !OriginalProperty(type, ordinal, original) ||
                !OriginalAccessor(type, original.get.Value, property.Getter) ||
                !OriginalAccessor(type, original.set.Value, property.Setter) ||
                property.Getter == null && property.Setter == null ||
                property.Name != property.DefaultName || property.Attributes != property.DefaultAttributes ||
                property.OverridePropertyType != null || !ReferenceEquals(property.DeclaringType, type))
                return false;
        }
        for (var ordinal = 0; ordinal < type.Events.Count; ordinal++)
        {
            var member = type.Events[ordinal];
            if (member.Definition is not { } original || !OriginalEvent(type, ordinal, original) ||
                !ValidTypeIndex(type.AppContext, original.typeIndex.Value) || original.RawType is not { } raw ||
                !RetainedDescriptor(type.AppContext, raw) ||
                !OriginalAccessor(type, original.add.Value, member.Adder) ||
                !OriginalAccessor(type, original.remove.Value, member.Remover) ||
                !OriginalAccessor(type, original.raise.Value, member.Invoker) ||
                member.Adder == null && member.Remover == null && member.Invoker == null ||
                member.Name != member.DefaultName || member.Attributes != member.DefaultAttributes ||
                member.OverrideEventType != null || !ReferenceEquals(member.DeclaringType, type))
                return false;
        }
        foreach (var field in type.Fields)
        {
            if (field.BackingData?.Field is not { } original || !OriginalField(type.AppContext, original) ||
                !ReferenceEquals(field.DeclaringType, type) ||
                !ReferenceEquals(original.DeclaringType, definition) || !ValidTypeIndex(type.AppContext, original.typeIndex.Value) ||
                original.RawFieldType is not { } raw || !RetainedDescriptor(type.AppContext, raw) ||
                field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes ||
                field.Offset != field.DefaultOffset || field.OverrideFieldType != null || field.UseOverrideConstantValue)
                return false;
            if (raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT &&
                !ReferenceEquals(ResolveClass(type.AppContext, raw), field.FieldType)) return false;
            if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY &&
                (!ReferenceDescriptor(raw) || raw.GetEncapsulatedType() is not { } element ||
                 !CoherentDescriptor(element) || !type.AppContext.Binary.TryGetTypeVirtualAddress(element, out var address) ||
                 address != raw.Data.Dummy ||
                 element.Type is Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT &&
                    ResolveClass(type.AppContext, element) == null)) return false;
        }
        return true;
    }

    private static bool OriginalAccessor(TypeAnalysisContext type, int localIndex, MethodAnalysisContext? accessor)
    {
        if (localIndex == -1) return accessor == null;
        if (localIndex < 0 || localIndex >= type.Methods.Count || accessor == null) return false;
        var index = (long)type.Definition!.FirstMethodIdx.Value + localIndex;
        return index >= 0 && index < type.AppContext.Metadata.MethodDefinitionCount &&
            ReferenceEquals(type.Methods[localIndex], accessor) && ReferenceEquals(accessor.DeclaringType, type) &&
            ReferenceEquals(type.AppContext.Metadata.methodDefs[(int)index], accessor.Definition);
    }

    internal static bool InstanceConstructor(MethodAnalysisContext method) =>
        method.Definition is { genericContainerIndex: { IsNull: true } } definition && OriginalMethod(method.AppContext, definition) &&
        ValidTypeIndex(method.AppContext, definition.returnTypeIdx.Value) &&
        definition.RawReturnType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0, Byref: 0, Pinned: 0, ValueType: 1 } raw &&
        RetainedDescriptor(method.AppContext, raw) && !method.IsStatic && !method.IsVirtual && method.IsVoid && method.GenericParameters.Count == 0 &&
        (method.Attributes & (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) ==
            (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0;

    // Uncalled framework siblings may legitimately have byref parameters. They
    // need coherent original descriptors and bounded identities, not the ABI
    // eligibility required of the selected caller and callee.
    internal static bool RetainedDescriptor(ApplicationAnalysisContext app, Il2CppType raw) =>
        CoherentDescriptor(raw) && OriginalDescriptor(app, raw) && (raw.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_CLASS => raw.ValueType == 0 &&
                raw.Data.Dummy < (ulong)app.Metadata.TypeDefinitionCount,
            Il2CppTypeEnum.IL2CPP_TYPE_OBJECT => raw.ValueType == 0 &&
                app.SystemTypes.SystemObjectType.Definition is { } definition &&
                definition.TypeIndex.Value >= 0 && raw.Data.Dummy == (ulong)definition.TypeIndex.Value,
            _ => true,
        });

    private static bool HasLifecycleEligibility(TypeAnalysisContext start, TypeAnalysisContext current, bool retainAncestorInitializers)
    {
        var cctors = current.Methods.Where(m => m.Name == ".cctor").ToArray();
        if (!current.Definition!.HasCctor) return cctors.Length == 0;
        return retainAncestorInitializers && !ReferenceEquals(start, current) && !ReferenceEquals(start.DeclaringAssembly, current.DeclaringAssembly) &&
            (current.Attributes & TypeAttributes.BeforeFieldInit) != 0 &&
            // This is output dependency policy, not an authenticity test. The complete
            // original identity must remain canonical and resolve separately at source validation.
            AsmResolverDllOutputFormatIlRecovery.IsReferenceAssembly(current.DeclaringAssembly.Name) &&
            CanonicalReference(start.DeclaringAssembly, current.DeclaringAssembly) && cctors is [var cctor] && StaticConstructor(cctor);
    }

    internal static bool StaticConstructor(MethodAnalysisContext method) =>
        method.Name == ".cctor" && method.Name == method.DefaultName && method.IsStatic && !method.IsVirtual && method.IsVoid &&
        method.Parameters.Count == 0 && method.GenericParameters.Count == 0 && method.OverrideReturnType == null &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) == (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0 &&
        method.Definition is { genericContainerIndex: { IsNull: true }, parameterCount: 0 } d && OriginalMethod(method.AppContext, d) &&
        d.InternalParameterData is [] && ValidTypeIndex(method.AppContext, d.returnTypeIdx.Value) &&
        d.RawReturnType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, Attrs: 0, NumMods: 0, Byref: 0, Pinned: 0, ValueType: 1 } raw && RetainedDescriptor(method.AppContext, raw) &&
        ReferenceEquals(d.DeclaringType, method.DeclaringType!.Definition);

}
