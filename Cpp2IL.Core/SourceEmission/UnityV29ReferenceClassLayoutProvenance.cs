using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Reports reference classes whose player metadata marks nondefault packing or
/// class size. A nondefault class-size flag does not directly retain the
/// authored managed Size, even when the declared packing is known.
/// </summary>
public static class UnityV29ReferenceClassLayoutProvenance
{
    internal const string DeclarationDiagnostic =
        "DECL003: Version-29 player records for selected reference classes with nondefault class-size flags do not directly encode the authored managed ClassLayout Size. Those declared sizes remain unknown.";

    public static IReadOnlyList<UnityReferenceClassLayoutAssemblyReport> Analyze(
        ApplicationAnalysisContext context, IEnumerable<string> selectedAssemblyNames)
    {
        if (!UnityV29ReturnMetadataProvenance.IsV29Family(context.MetadataVersion))
            return [];

        var result = new List<UnityReferenceClassLayoutAssemblyReport>();
        foreach (var name in selectedAssemblyNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            if (!context.AssembliesByName.TryGetValue(name, out var assembly))
                throw new ArgumentException($"Selected assembly is absent from the analyzed player: {name}", nameof(selectedAssemblyNames));

            var types = new List<UnityReferenceClassLayoutTypeReport>();
            foreach (var type in assembly.Types)
            {
                var definition = type.Definition;
                if (definition == null || !HasNondefaultClassLayout(definition))
                    continue;

                types.Add(CreateTypeReport(type.FullName!, definition, definition.Size));
            }

            types.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            if (types.Count != 0)
                result.Add(new UnityReferenceClassLayoutAssemblyReport(name, types));
        }

        return result;
    }

    internal static void AddToReport(UnitySourceEmissionReport report, IReadOnlyList<UnityReferenceClassLayoutAssemblyReport> layouts)
    {
        report.ReferenceClassLayoutMetadata.AddRange(layouts);
        if (layouts.Count == 0)
            return;

        if (layouts.Any(layout => layout.UnknownDeclaredClassSizeCount != 0))
        {
            report.DeclarationFidelity = "partial";
            report.DeclarationDiagnostics.Add(DeclarationDiagnostic);
        }
        foreach (var layout in layouts)
        {
            if (layout.UnemittedClassLayoutCount == 0)
                continue;

            report.DeclarationFidelity = "partial";
            if (report.SourceGeneration == "generated")
                report.SourceGeneration = "partial";
            report.Diagnostics.Add($"SOURCE011: {layout.Name}: Managed ClassLayout rows were not emitted for {layout.UnemittedClassLayoutCount} selected reference classes with nondefault packing or class-size flags; generated source cannot preserve their layout declarations.");
        }
    }

    internal static bool HasUnemittedClassLayout(Il2CppTypeDefinition definition) =>
        HasNondefaultClassLayout(definition) &&
        !AsmResolverDllOutputFormat.TryGetReferenceClassPack(definition, out _);

    private static bool HasNondefaultClassLayout(Il2CppTypeDefinition definition) =>
        !definition.IsValueType && !definition.IsEnumType && !definition.IsInterface &&
        (!definition.PackingSizeIsDefault || !definition.ClassSizeIsDefault);

    internal static UnityReferenceClassLayoutTypeReport CreateTypeReport(
        string name, Il2CppTypeDefinition definition, int rawNativeSize) =>
        new(name, rawNativeSize, definition.PackingSizeIsDefault, definition.ClassSizeIsDefault,
            definition.PackingSize, definition.SpecifiedPackingSize,
            AsmResolverDllOutputFormat.TryGetReferenceClassPack(definition, out _));
}
