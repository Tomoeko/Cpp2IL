using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Model.CustomAttributes;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Finds v29 custom-attribute Type values whose original serialized name qualification
/// cannot be reconstructed from the player metadata's type index alone.
/// </summary>
internal static class UnityV29AttributeTypeProvenance
{
    private static readonly SignatureComparer ExactTypeComparer = new(SignatureComparisonFlags.ExactVersion);

    internal sealed record EmissionAssessment(string AssemblyName, int IndexedValueCount, bool RetainedArgumentsEmitted);

    /// <summary>
    /// The v29 index determines the Type object used by IL2CPP. Only downgrade the
    /// missing serialized-name spelling to a declaration diagnostic after the exact
    /// indexed values and their retained sibling arguments have reached the recovered
    /// managed attribute signatures. This does not prove the serialized Type-name spelling.
    /// </summary>
    internal static IEnumerable<EmissionAssessment> AssessEmission(ApplicationAnalysisContext context,
        IEnumerable<string> selectedAssemblyNames)
    {
        foreach (var name in selectedAssemblyNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            if (!context.AssembliesByName.TryGetValue(name, out var assembly))
                continue;

            var indexedValueCount = 0;
            var retainedArgumentsEmitted = true;
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
                    retainedArgumentsEmitted = false;
                    continue;
                }

                for (var index = 0; index < analyzed.Count; index++)
                {
                    if (CountIndexedTypeValues(analyzed[index]) != 0 &&
                        !HasMatchingRetainedArguments(analyzed[index], emitted[index]))
                        retainedArgumentsEmitted = false;
                }
            }

            if (indexedValueCount != 0)
                yield return new EmissionAssessment(name, indexedValueCount, retainedArgumentsEmitted);
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

    internal static bool HasMatchingRetainedArguments(AnalyzedCustomAttribute source, CustomAttribute emitted)
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
            if (!MatchesRetainedArgument(source.ConstructorParameters[index], signature.FixedArguments[index], declaredType))
                return false;
        }

        var namedIndex = 0;
        foreach (var field in source.Fields)
        {
            var named = signature.NamedArguments[namedIndex++];
            var declaredType = field.Field.ToTypeSignature();
            if (named.MemberType != CustomAttributeArgumentMemberType.Field || named.MemberName != field.Field.Name ||
                !ExactTypeComparer.Equals(named.ArgumentType, declaredType) ||
                !MatchesRetainedArgument(field.Value, named.Argument, declaredType))
                return false;
        }
        foreach (var property in source.Properties)
        {
            var named = signature.NamedArguments[namedIndex++];
            var declaredType = property.Property.ToTypeSignature();
            if (named.MemberType != CustomAttributeArgumentMemberType.Property || named.MemberName != property.Property.Name ||
                !ExactTypeComparer.Equals(named.ArgumentType, declaredType) ||
                !MatchesRetainedArgument(property.Value, named.Argument, declaredType))
                return false;
        }
        return true;
    }

    internal static bool MatchesRetainedArgument(BaseCustomAttributeParameter source, CustomAttributeArgument emitted,
        TypeSignature declaredType)
    {
        if (!ExactTypeComparer.Equals(emitted.ArgumentType, declaredType))
            return false;
        if (source is not CustomAttributeArrayParameter &&
            (emitted.IsNullArray || emitted.Elements.Count != 1))
            return false;
        if (source is CustomAttributeArrayParameter { IsNullArray: true } &&
            declaredType is not SzArrayTypeSignature)
            return false;

        var systemObject = source.Owner.Constructor.AppContext.SystemTypes.SystemObjectType.ToTypeSignature();
        if (ExactTypeComparer.Equals(declaredType, systemObject))
        {
            return emitted.Elements.Count == 1 && emitted.Element is BoxedArgument boxed &&
                   MatchesRetainedValue(source, boxed.Value, boxed.Type);
        }

        return MatchesRetainedValue(source,
            source is CustomAttributeArrayParameter ? emitted.IsNullArray ? null : emitted.Elements.ToArray() : emitted.Element,
            declaredType);
    }

    private static bool MatchesRetainedValue(BaseCustomAttributeParameter source, object? value,
        TypeSignature valueType)
    {
        var systemTypes = source.Owner.Constructor.AppContext.SystemTypes;
        if (source is CustomAttributeTypeParameter type)
        {
            if (!ExactTypeComparer.Equals(valueType, systemTypes.SystemTypeType.ToTypeSignature()))
                return false;
            if (!type.HasNonNullV29TypeIndex)
                return type.TypeContext == null && value == null;
            return type.TypeContext is { } context && value is TypeSignature emittedType &&
                   ExactTypeComparer.Equals(context.ToTypeSignature(), emittedType);
        }

        if (source is CustomAttributePrimitiveParameter primitive)
            return TryGetPrimitiveType(primitive.PrimitiveType, source, out var primitiveType) &&
                   ExactTypeComparer.Equals(valueType, primitiveType) &&
                   MatchesPrimitiveValue(primitive.PrimitiveValue, value);

        if (source is CustomAttributeEnumParameter enumParameter)
        {
            var enumType = source.Owner.Constructor.AppContext.ResolveIl2CppType(enumParameter.EnumType);
            return enumType != null && ExactTypeComparer.Equals(valueType, enumType.ToTypeSignature()) &&
                   MatchesPrimitiveValue(enumParameter.UnderlyingPrimitiveParameter.PrimitiveValue, value);
        }

        if (source is not CustomAttributeArrayParameter array || valueType is not SzArrayTypeSignature emittedArray)
            return false;

        if (array.IsNullArray)
            // The v29 null-array payload has no element type. The declared owner
            // supplies it for top-level arguments; nested null arrays cannot be certified.
            return array.Kind != CustomAttributeParameterKind.ArrayElement && value == null;

        if (value is not object?[] elements || elements.Length != array.ArrayElements.Count)
            return false;

        TypeSignature? expectedElementType;
        if (array.EnumType != null)
        {
            var enumType = source.Owner.Constructor.AppContext.ResolveIl2CppType(array.EnumType);
            if (enumType == null)
                return false;
            expectedElementType = enumType.ToTypeSignature();
        }
        else if (array.ArrType == Il2CppTypeEnum.IL2CPP_TYPE_IL2CPP_TYPE_INDEX)
            expectedElementType = systemTypes.SystemTypeType.ToTypeSignature();
        else if (!TryGetPrimitiveType(array.ArrType, source, out expectedElementType))
            return false;

        if (!ExactTypeComparer.Equals(emittedArray.BaseType, expectedElementType))
            return false;

        var objectElements = array.ArrType == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT && array.EnumType == null;
        for (var index = 0; index < elements.Length; index++)
        {
            if (objectElements)
            {
                if (elements[index] is not BoxedArgument boxed ||
                    !MatchesRetainedValue(array.ArrayElements[index], boxed.Value, boxed.Type))
                    return false;
            }
            else if (array.EnumType != null)
            {
                if (array.ArrayElements[index] is not CustomAttributePrimitiveParameter enumElement ||
                    enumElement.PrimitiveType != array.ArrType || elements[index] is BoxedArgument ||
                    !MatchesPrimitiveValue(enumElement.PrimitiveValue, elements[index]))
                    return false;
            }
            else if (elements[index] is BoxedArgument ||
                     !MatchesRetainedValue(array.ArrayElements[index], elements[index], expectedElementType))
                return false;
        }
        return true;
    }

    private static bool TryGetPrimitiveType(Il2CppTypeEnum kind, BaseCustomAttributeParameter source,
        out TypeSignature? type)
    {
        try
        {
            type = source.Owner.Constructor.AppContext.SystemTypes.GetPrimitive(kind).ToTypeSignature();
            return true;
        }
        catch (ArgumentException)
        {
            type = null;
            return false;
        }
    }

    private static bool MatchesPrimitiveValue(IConvertible? expected, object? emitted) => expected switch
    {
        null => emitted == null,
        float single => emitted is float value && SingleBits(single) == SingleBits(value),
        double number => emitted is double value && BitConverter.DoubleToInt64Bits(number) == BitConverter.DoubleToInt64Bits(value),
        _ => expected.GetType() == emitted?.GetType() && expected.Equals(emitted),
    };

    private static int SingleBits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

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
