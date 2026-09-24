using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Records what a version-29 player can establish about method return parameters.
/// A missing return-parameter token is not evidence that the managed method had no
/// sequence-zero parameter row or return custom attributes.
/// </summary>
public static class UnityV29ReturnMetadataProvenance
{
    public static IReadOnlyList<UnityReturnMetadataAssemblyReport> Analyze(
        ApplicationAnalysisContext context, IEnumerable<string> selectedAssemblyNames)
    {
        if (!IsV29Family(context.MetadataVersion))
            return [];

        var result = new List<UnityReturnMetadataAssemblyReport>();
        foreach (var name in selectedAssemblyNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            if (!context.AssembliesByName.TryGetValue(name, out var assembly))
                throw new ArgumentException($"Selected assembly is absent from the analyzed player: {name}", nameof(selectedAssemblyNames));

            var playerMethodCount = assembly.Types.SelectMany(type => type.Methods)
                .Count(method => method.Definition != null);
            if (playerMethodCount == 0)
                continue;

            result.Add(new UnityReturnMetadataAssemblyReport(name, playerMethodCount,
                playerMethodCount, playerMethodCount,
                UnityReturnMetadataAvailability.UnknownInPlayer,
                UnityReturnMetadataAvailability.UnknownInPlayer));
        }

        return result;
    }

    internal static bool IsV29Family(float metadataVersion) => metadataVersion is >= 29f and < 30f;
}
