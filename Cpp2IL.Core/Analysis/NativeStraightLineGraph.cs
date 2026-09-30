using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>Requires every operation in a closed linear proof to belong to the emitted body.</summary>
internal static class NativeStraightLineGraph
{
    internal static bool TryGetBody(MethodAnalysisContext method, out IReadOnlyList<Instruction> instructions)
    {
        instructions = [];
        if (method.ControlFlowGraph is not { } graph || graph.Blocks.Count != 3 ||
            graph.EntryBlock.Instructions.Count != 0 || graph.ExitBlock.Instructions.Count != 0 ||
            graph.EntryBlock.Predecessors.Count != 0 || graph.ExitBlock.Successors.Count != 0 ||
            graph.EntryBlock.Successors is not [var body] || ReferenceEquals(body, graph.ExitBlock) ||
            body.Predecessors is not [var entry] || !ReferenceEquals(entry, graph.EntryBlock) ||
            body.Successors is not [var exit] || !ReferenceEquals(exit, graph.ExitBlock) ||
            graph.ExitBlock.Predecessors is not [var predecessor] || !ReferenceEquals(predecessor, body) ||
            new[] { graph.EntryBlock, body, graph.ExitBlock }.Any(expected =>
                graph.Blocks.Count(block => ReferenceEquals(block, expected)) != 1))
            return false;
        instructions = body.Instructions.ToArray();
        return true;
    }
}
