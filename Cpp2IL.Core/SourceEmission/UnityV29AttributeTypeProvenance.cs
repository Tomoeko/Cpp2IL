using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Model.CustomAttributes;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Finds v29 custom-attribute Type values whose original serialized name qualification
/// cannot be reconstructed from the player metadata's type index alone.
/// </summary>
internal static class UnityV29AttributeTypeProvenance
{
    private static readonly SignatureComparer ExactTypeComparer = new(SignatureComparisonFlags.ExactVersion);

    internal sealed record EmissionAssessment(string AssemblyName, int IndexedValueCount, bool IdentitiesEmitted);

    /// <summary>
    /// The v29 index determines the Type object used by IL2CPP. Only downgrade the
    /// missing serialized-name spelling to a declaration diagnostic after the exact
    /// indexed values have also reached the recovered managed attribute signatures.
    /// </summary>
    internal static IEnumerable<EmissionAssessment> AssessEmission(ApplicationAnalysisContext context,
        IEnumerable<string> selectedAssemblyNames)
    {
        foreach (var name in selectedAssemblyNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            if (!context.AssembliesByName.TryGetValue(name, out var assembly))
                continue;

            var indexedValueCount = 0;
            var identitiesEmitted = true;
            foreach (var (owner, emitted) in EnumerateAttributeOwnerPairs(assembly))
            {
                var analyzed = owner.CustomAttributes;
                if (analyzed == null)
                    continue;
                var ownerValueCount = analyzed.Sum(CountIndexedTypeValues);
                if (ownerValueCount == 0)
                    continue;

                indexedValueCount += ownerValueCount;
                if (emitted == null || emitted.Count != analyzed.Count)
                {
                    identitiesEmitted = false;
                    continue;
                }

                for (var index = 0; index < analyzed.Count; index++)
                {
                    if (CountIndexedTypeValues(analyzed[index]) != 0 &&
                        !HasMatchingIndexedTypeValues(analyzed[index], emitted[index]))
                        identitiesEmitted = false;
                }
            }

            if (indexedValueCount != 0)
                yield return new EmissionAssessment(name, indexedValueCount, identitiesEmitted);
        }
    }

    public static IEnumerable<string> GetAffectedAssemblyNames(ApplicationAnalysisContext context,
        IEnumerable<string> selectedAssemblyNames)
    {
        foreach (var name in selectedAssemblyNames.Distinct(System.StringComparer.Ordinal).OrderBy(name => name, System.StringComparer.Ordinal))
        {
            if (context.AssembliesByName.TryGetValue(name, out var assembly) &&
                EnumerateAttributeOwners(assembly).Any(owner => ContainsTypeValueWithUnknownSerialization(owner.CustomAttributes)))
                yield return name;
        }
    }

    internal static bool ContainsTypeValueWithUnknownSerialization(IEnumerable<AnalyzedCustomAttribute>? attributes) =>
        attributes?.Any(attribute => CountIndexedTypeValues(attribute) != 0) ?? false;

    private static int CountIndexedTypeValues(AnalyzedCustomAttribute attribute) =>
        attribute.ConstructorParameters.Sum(CountIndexedTypeValues) +
        attribute.Fields.Sum(field => CountIndexedTypeValues(field.Value)) +
        attribute.Properties.Sum(property => CountIndexedTypeValues(property.Value));

    private static int CountIndexedTypeValues(BaseCustomAttributeParameter parameter) => parameter switch
    {
        CustomAttributeTypeParameter { HasNonNullV29TypeIndex: true } => 1,
        CustomAttributeArrayParameter { IsNullArray: false } array => array.ArrayElements.Sum(CountIndexedTypeValues),
        _ => 0,
    };

    internal static bool HasMatchingIndexedTypeValues(AnalyzedCustomAttribute source, CustomAttribute emitted)
    {
        if (!source.IsSuitableForEmission ||
            !ReferenceEquals(source.Constructor.GetExtraData<MethodDefinition>("AsmResolverMethod"), emitted.Constructor))
            return false;

        var signature = emitted.Signature;
        if (signature == null || source.Constructor.Parameters.Count != source.ConstructorParameters.Count ||
            signature.FixedArguments.Count != source.ConstructorParameters.Count ||
            signature.NamedArguments.Count != source.Fields.Count + source.Properties.Count)
            return false;

        for (var index = 0; index < source.ConstructorParameters.Count; index++)
        {
            if (source.ConstructorParameters[index].Index != index)
                return false;
            var declaredType = source.Constructor.Parameters[index].ToTypeSignature();
            if (!MatchesIndexedTypeValues(source.ConstructorParameters[index], signature.FixedArguments[index], declaredType))
                return false;
        }

        var namedIndex = 0;
        foreach (var field in source.Fields)
        {
            var named = signature.NamedArguments[namedIndex++];
            var declaredType = field.Field.ToTypeSignature();
            if (named.MemberType != CustomAttributeArgumentMemberType.Field || named.MemberName != field.Field.Name ||
                !ExactTypeComparer.Equals(named.ArgumentType, declaredType) ||
                !MatchesIndexedTypeValues(field.Value, named.Argument, declaredType))
                return false;
        }
        foreach (var property in source.Properties)
        {
            var named = signature.NamedArguments[namedIndex++];
            var declaredType = property.Property.ToTypeSignature();
            if (named.MemberType != CustomAttributeArgumentMemberType.Property || named.MemberName != property.Property.Name ||
                !ExactTypeComparer.Equals(named.ArgumentType, declaredType) ||
                !MatchesIndexedTypeValues(property.Value, named.Argument, declaredType))
                return false;
        }
        return true;
    }

    internal static bool MatchesIndexedTypeValues(BaseCustomAttributeParameter source, CustomAttributeArgument emitted,
        TypeSignature declaredType)
    {
        if (CountIndexedTypeValues(source) == 0)
            return true;

        if (!ExactTypeComparer.Equals(emitted.ArgumentType, declaredType))
            return false;

        var systemObject = source.Owner.Constructor.AppContext.SystemTypes.SystemObjectType.ToTypeSignature();
        if (ExactTypeComparer.Equals(declaredType, systemObject))
        {
            return emitted.Elements.Count == 1 && emitted.Element is BoxedArgument boxed &&
                   MatchesIndexedTypeValue(source, boxed.Value, boxed.Type);
        }

        return MatchesIndexedTypeValue(source,
            source is CustomAttributeArrayParameter ? emitted.IsNullArray ? null : emitted.Elements.ToArray() : emitted.Element,
            declaredType);
    }

    private static bool MatchesIndexedTypeValue(BaseCustomAttributeParameter source, object? value,
        TypeSignature valueType)
    {
        if (CountIndexedTypeValues(source) == 0)
            return true;

        var systemTypes = source.Owner.Constructor.AppContext.SystemTypes;
        if (source is CustomAttributeTypeParameter type)
        {
            return ExactTypeComparer.Equals(valueType, systemTypes.SystemTypeType.ToTypeSignature()) &&
                   type.TypeContext is { } context && value is TypeSignature emittedType &&
                   ExactTypeComparer.Equals(context.ToTypeSignature(), emittedType);
        }

        if (source is not CustomAttributeArrayParameter { IsNullArray: false } array ||
            valueType is not SzArrayTypeSignature emittedArray || value is not object?[] elements ||
            elements.Length != array.ArrayElements.Count)
            return false;

        var expectedElementType = array.ArrType switch
        {
            LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_OBJECT => systemTypes.SystemObjectType.ToTypeSignature(),
            LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_IL2CPP_TYPE_INDEX => systemTypes.SystemTypeType.ToTypeSignature(),
            _ => null,
        };
        if (expectedElementType == null || !ExactTypeComparer.Equals(emittedArray.BaseType, expectedElementType))
            return false;

        var objectElements = ExactTypeComparer.Equals(expectedElementType, systemTypes.SystemObjectType.ToTypeSignature());
        for (var index = 0; index < elements.Length; index++)
        {
            if (objectElements)
            {
                if (elements[index] is not BoxedArgument boxed ||
                    !MatchesIndexedTypeValue(array.ArrayElements[index], boxed.Value, boxed.Type))
                    return false;
            }
            else if (elements[index] is BoxedArgument ||
                     !MatchesIndexedTypeValue(array.ArrayElements[index], elements[index], expectedElementType))
                return false;
        }
        return true;
    }

    private static IEnumerable<(HasCustomAttributes Owner, IReadOnlyList<CustomAttribute>? Emitted)>
        EnumerateAttributeOwnerPairs(AssemblyAnalysisContext assembly)
    {
        var managed = assembly.GetExtraData<AssemblyDefinition>("AsmResolverAssembly");
        yield return (assembly, managed?.CustomAttributes.ToArray());
        yield return (assembly.ManifestModule, managed?.ManifestModule?.CustomAttributes.ToArray());

        foreach (var type in assembly.Types)
        {
            var managedType = type.GetExtraData<TypeDefinition>("AsmResolverType");
            yield return (type, managedType?.CustomAttributes.ToArray());
            foreach (var method in type.Methods)
            {
                var managedMethod = method.GetExtraData<MethodDefinition>("AsmResolverMethod");
                yield return (method, managedMethod?.CustomAttributes.ToArray());
                foreach (var parameter in method.Parameters)
                {
                    var definitions = managedMethod?.ParameterDefinitions;
                    var emitted = definitions != null && parameter.ParameterIndex < definitions.Count
                        ? definitions[parameter.ParameterIndex].CustomAttributes.ToArray() : null;
                    yield return (parameter, emitted);
                }
            }
            foreach (var field in type.Fields)
                yield return (field, field.GetExtraData<FieldDefinition>("AsmResolverField")?.CustomAttributes.ToArray());
            foreach (var property in type.Properties)
                yield return (property, property.GetExtraData<PropertyDefinition>("AsmResolverProperty")?.CustomAttributes.ToArray());
            foreach (var eventDefinition in type.Events)
                yield return (eventDefinition, eventDefinition.GetExtraData<EventDefinition>("AsmResolverEvent")?.CustomAttributes.ToArray());
        }
    }

    private static IEnumerable<HasCustomAttributes> EnumerateAttributeOwners(AssemblyAnalysisContext assembly)
    {
        yield return assembly;
        yield return assembly.ManifestModule;

        foreach (var type in assembly.Types)
        {
            yield return type;
            foreach (var field in type.Fields)
                yield return field;
            foreach (var method in type.Methods)
            {
                yield return method;
                foreach (var parameter in method.Parameters)
                    yield return parameter;
            }
            foreach (var property in type.Properties)
                yield return property;
            foreach (var eventDefinition in type.Events)
                yield return eventDefinition;
        }
    }
}
