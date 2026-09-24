using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Model.CustomAttributes;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Finds v29 custom-attribute Type values whose original serialized name qualification
/// cannot be reconstructed from the player metadata's type index alone.
/// </summary>
internal static class UnityV29AttributeTypeProvenance
{
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
        attributes?.Any(attribute =>
            attribute.ConstructorParameters.Any(ContainsTypeValueWithUnknownSerialization) ||
            attribute.Fields.Any(field => ContainsTypeValueWithUnknownSerialization(field.Value)) ||
            attribute.Properties.Any(property => ContainsTypeValueWithUnknownSerialization(property.Value))) ?? false;

    private static bool ContainsTypeValueWithUnknownSerialization(BaseCustomAttributeParameter parameter) => parameter switch
    {
        CustomAttributeTypeParameter type => type.HasNonNullV29TypeIndex,
        CustomAttributeArrayParameter { IsNullArray: false } array => array.ArrayElements.Any(ContainsTypeValueWithUnknownSerialization),
        _ => false,
    };

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
