using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Records value-type ClassLayout sizes for which this version-29 player-only pass has
/// no proof of the authored managed Size. This does not cover reference-class layout.
/// The player type record does not directly encode the authored ClassLayout Size.
/// </summary>
public static class UnityV29ValueTypeClassLayoutProvenance
{
    internal const string Diagnostic =
        "DECL002: Version-29 player records for selected value types do not directly encode the authored managed ClassLayout Size, and this pass has not proved it from layout. Emitted Size values remain unresolved candidates, not recovered declared sizes. Reference-class layout is outside this check.";

    public static IReadOnlyList<UnityValueTypeClassLayoutAssemblyReport> Analyze(
        ApplicationAnalysisContext context, IEnumerable<string> selectedAssemblyNames)
    {
        if (!UnityV29ReturnMetadataProvenance.IsV29Family(context.MetadataVersion))
            return [];

        var result = new List<UnityValueTypeClassLayoutAssemblyReport>();
        foreach (var name in selectedAssemblyNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            if (!context.AssembliesByName.TryGetValue(name, out var assembly))
                throw new ArgumentException($"Selected assembly is absent from the analyzed player: {name}", nameof(selectedAssemblyNames));

            var types = new List<UnityValueTypeClassLayoutTypeReport>();
            foreach (var type in assembly.Types)
            {
                var definition = type.Definition;
                if (definition == null || !HasUnknownDeclaredClassSize(definition))
                    continue;

                var rawNativeSize = definition.Size;
                types.Add(new UnityValueTypeClassLayoutTypeReport(type.FullName!, rawNativeSize));
            }
            types.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            if (types.Count != 0)
                result.Add(new UnityValueTypeClassLayoutAssemblyReport(name, types));
        }

        return result;
    }

    internal static void AddToReport(UnitySourceEmissionReport report, IReadOnlyList<UnityValueTypeClassLayoutAssemblyReport> layouts)
    {
        report.ValueTypeClassLayoutMetadata.AddRange(layouts);
        if (layouts.Count == 0)
            return;

        report.DeclarationFidelity = "partial";
        report.DeclarationDiagnostics.Add(Diagnostic);
    }

    internal static bool HasUnknownDeclaredClassSize(Il2CppTypeDefinition definition) =>
        definition.IsValueType && !definition.IsEnumType && !definition.ClassSizeIsDefault;
}
