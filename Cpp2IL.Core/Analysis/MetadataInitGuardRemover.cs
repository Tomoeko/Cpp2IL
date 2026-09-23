using System.Collections.Generic;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Compatibility entry points for legacy initialization analysis and shared CFG excision.
/// Class initialization can execute managed code or throw. Offsets, region shape and helper names
/// alone do not prove that deleting it preserves the required managed initialization behavior.
/// Positive metadata-only proofs belong before native helper provenance is discarded.
/// </summary>
public static class MetadataInitGuardRemover
{
    /// <summary>Preserves initialization calls until their managed effects can be represented.</summary>
    public static void Run(MethodAnalysisContext method) { }

    /// <summary>Preserves RGCTX initialization; an untyped offset is not evidence about its callee.</summary>
    public static void RunRgctx(MethodAnalysisContext method) { }

    /// <summary>Retained for API compatibility; an offset alone cannot authorize guard removal.</summary>
    public static void Run(ISILControlFlowGraph cfg, long initialisedFlagOffset) { }

    /// <summary>Helper names alone do not prove that an initialization call can become a value move.</summary>
    public static void RewriteUnguardedInits(MethodAnalysisContext method) { }

    internal static void Excise(ISILControlFlowGraph cfg, Block guard, Block initEntry, Block merge, HashSet<Block> region)
    {
        // 1. Repair the merge's phis: drop the inputs from the region's back-edges.
        for (var i = merge.Predecessors.Count - 1; i >= 0; i--)
        {
            if (!region.Contains(merge.Predecessors[i]))
                continue;

            foreach (var phi in merge.Instructions)
                if (phi.OpCode == OpCode.Phi && 1 + i < phi.Operands.Count)
                    phi.RemoveOperandAt(1 + i);

            merge.Predecessors.RemoveAt(i);
        }

        // 2. Fold the guard so it goes straight to the merge.
        guard.Successors.Remove(initEntry);
        initEntry.Predecessors.Remove(guard);

        var terminator = guard.Instructions[^1];
        terminator.OpCode = OpCode.Jump;
        terminator.SetOperands(merge);
        guard.CalculateBlockType();

        // 3. Delete the region. 
        foreach (var block in region)
        {
            foreach (var successor in block.Successors)
                successor.Predecessors.Remove(block);
            foreach (var predecessor in block.Predecessors)
                predecessor.Successors.Remove(block);

            block.Successors.Clear();
            block.Predecessors.Clear();
            cfg.Blocks.Remove(block);
        }
    }
}
