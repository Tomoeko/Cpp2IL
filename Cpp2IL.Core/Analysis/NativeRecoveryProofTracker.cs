using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>Retains proof admission when mutable analysis data or register names are lost.</summary>
internal static class NativeRecoveryProofTracker
{
    private static readonly ConditionalWeakTable<MethodAnalysisContext, HashSet<string>> Proofs = new();

    internal static void Mark(MethodAnalysisContext method, string proofId)
    {
        var recorded = Proofs.GetValue(method, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (recorded)
            recorded.Add(proofId);
    }

    internal static bool Has(MethodAnalysisContext method, string proofId)
    {
        if (!Proofs.TryGetValue(method, out var recorded))
            return false;
        lock (recorded)
            return recorded.Contains(proofId);
    }
}
