using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64NativeNullCheckedInvocationProof
{
    internal static bool TryReachingDefinition(ISILControlFlowGraph graph,
        IReadOnlyCollection<LocalVariable> incoming, LocalVariable value, Instruction use,
        out Instruction? definition)
    {
        definition = null;
        if (graph.FindBlockByInstruction(use) is not { } block) return false;
        var visiting = new HashSet<Block>();
        var proved = new Dictionary<Block, Instruction?>();
        return Before(block, block.Instructions.IndexOf(use), out definition);

        bool Before(Block current, int limit, out Instruction? found)
        {
            found = null;
            var fullBlock = limit == current.Instructions.Count;
            // A completed proof is independent of the active recursion path.
            // Share only successful full-block results within this one query;
            // null denotes a proved incoming value, not an unknown definition.
            if (fullBlock && proved.TryGetValue(current, out found)) return true;
            for (var index = limit - 1; index >= 0; index--)
                if (ReferenceEquals(current.Instructions[index].Destination, value))
                {
                    found = current.Instructions[index];
                    if (fullBlock) proved[current] = found;
                    return true;
                }
            if (current == graph.EntryBlock)
            {
                if (!incoming.Contains(value)) return false;
                if (fullBlock) proved[current] = null;
                return true;
            }
            if (!visiting.Add(current)) return false;
            var first = true;
            foreach (var previous in current.Predecessors)
            {
                if (!Before(previous, previous.Instructions.Count, out var input) ||
                    !first && !ReferenceEquals(input, found))
                {
                    visiting.Remove(current);
                    return false;
                }
                found = input;
                first = false;
            }
            visiting.Remove(current);
            if (first) return false;
            if (fullBlock) proved[current] = found;
            return true;
        }
    }
}
