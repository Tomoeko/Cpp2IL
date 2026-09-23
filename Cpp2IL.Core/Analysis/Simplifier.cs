using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Post-SSA copy/constant cleanup. The bulk of copy and constant propagation is done earlier, in SSA,
/// by <see cref="SsaSimplifier"/>; this pass mops up the copies that <em>destroying</em> SSA leaves
/// behind - each phi is lowered to a <c>Move</c> on every incoming edge, so the phi's result becomes a
/// single local with one definition per predecessor.
///
/// Those multiple definitions mean a value is no longer single-assignment: at the join the definitions
/// merge, and which one reaches a use is path-dependent. So unlike <see cref="SsaSimplifier"/>, this
/// pass cannot blindly forward a definition - it walks the CFG and refuses to carry a multiply-defined
/// local's value across the join its other definitions merge at (where the phi used to be).
/// </summary>
public static class Simplifier
{
    public static void Simplify(MethodAnalysisContext method)
    {
        new SimplifierContext(method).Process();
    }

    private readonly ref struct SimplifierContext(MethodAnalysisContext method)
    {
        private readonly MethodAnalysisContext _method = method;
        private readonly ISILControlFlowGraph _graph = method.ControlFlowGraph!;
        private readonly HashSet<LocalVariable> _mutableStorage = OperandEffects.LocalsWithMutableStorage(method.ControlFlowGraph!.Instructions);

        public void Process()
        {
            InlineLocals();

            // Repeat until no change
            while (InlineConstantsSinglePass()) ;

            // More locals can now be inlined
            InlineLocals();

            _graph.RemoveNops();
            _graph.RemoveEmptyBlocks();
        }

        private bool InlineConstantsSinglePass()
        {
            var changed = false;
            var definitionCounts = CountDefinitions();

            var visited = new HashSet<Block>();
            var queue = new Queue<Block>(_graph.Blocks.Count);

#if NET5_0_OR_GREATER
            visited.EnsureCapacity(_graph.Blocks.Count);
#endif

            queue.Enqueue(_graph.EntryBlock);
            visited.Add(_graph.EntryBlock);

            while (queue.Count > 0)
            {
                var block = queue.Dequeue();

                for (var i = 0; i < block.Instructions.Count; i++)
                {
                    var instruction = block.Instructions[i];

                    // If it's move and it moves something to local, replace and remove it
                    if (instruction.OpCode == OpCode.Move && instruction.IntegerBitWidth == 0
                        && instruction.Operands[0] is LocalVariable local
                        && !_mutableStorage.Contains(local)
                        && OperandEffects.IsPureValue(instruction.Operands[1])
                        && (instruction.Operands[1] is not LocalVariable source || !_mutableStorage.Contains(source)))
                    {
                        if (IsLocalUsedAfterInstruction(block, i + 1, local, out var usedByMemory))
                        {
                            // This can't be inlined into memory operand
                            if (usedByMemory) continue;

                            // A local with several definitions is not in SSA form, so its value at a join
                            // depends on the path taken; don't carry this definition across that join.
                            var stopAtJoins = (definitionCounts.TryGetValue(local, out var defs) && defs > 1)
                                || (instruction.Operands[1] is LocalVariable copied && definitionCounts.ContainsKey(copied));

                            // Replace local
                            ReplaceLocalsUntilReassignment(block, i + 1, local, instruction.Operands[1], stopAtJoins);

                            // Only drop the defining move once the local has no remaining uses; if the
                            // replacement stopped at a join, the local is still live past it so the move stays.
                            if (IsLocalUsedAfterInstruction(block, i + 1, local, out _))
                                continue;

                            // Change that move to nop
                            instruction.OpCode = OpCode.Nop;
                            instruction.SetOperands();

                            changed = true;
                        }
                    }
                }

                foreach (var successor in block.Successors)
                {
                    if (visited.Add(successor))
                        queue.Enqueue(successor);
                }
            }

            return changed;
        }

        private void InlineLocals()
        {
            var definitionCounts = CountDefinitions();

            var visited = new HashSet<Block>();
            var queue = new Queue<Block>(_method.ControlFlowGraph!.Blocks.Count);

#if NET5_0_OR_GREATER
            visited.EnsureCapacity(_graph.Blocks.Count);
#endif

            queue.Enqueue(_graph.EntryBlock);
            visited.Add(_graph.EntryBlock);

            while (queue.Count > 0)
            {
                var block = queue.Dequeue();

                for (var i = 0; i < block.Instructions.Count; i++)
                {
                    var instruction = block.Instructions[i];

                    // If it's move and it moves local to local, replace and remove it
                    if (instruction is { OpCode: OpCode.Move, IntegerBitWidth: 0, Operands: [LocalVariable local, LocalVariable source] }
                        && !_mutableStorage.Contains(local) && !_mutableStorage.Contains(source))
                    {
                        // A local with several definitions is not in SSA form, so its value at a join
                        // depends on the path taken; don't carry this definition across that join.
                        var stopAtJoins = (definitionCounts.TryGetValue(local, out var defs) && defs > 1)
                            || definitionCounts.ContainsKey(source);

                        // Replace local with source
                        ReplaceLocalsUntilReassignment(block, i + 1, local, source, stopAtJoins);

                        // If the replacement stopped at a join merging another definition, the local is
                        // still live there - keep its defining move rather than dropping the value on this path.
                        if (IsLocalUsedAfterInstruction(block, i + 1, local, out _))
                            continue;

                        if (!_method.ParameterLocals.Contains(local))
                            _method.Locals.Remove(local);

                        // Change that move to nop
                        instruction.OpCode = OpCode.Nop;
                        instruction.SetOperands();
                    }
                }

                foreach (var successor in block.Successors)
                {
                    if (visited.Add(successor))
                        queue.Enqueue(successor);
                }
            }
        }

        // Counts how many instructions define each local. A local with more than one definition is not in
        // SSA form: at a control-flow join its value depends on which predecessor was taken, so none of its
        // definitions may be propagated across that join - a phi would be needed there instead.
        private Dictionary<LocalVariable, int> CountDefinitions()
        {
            var counts = new Dictionary<LocalVariable, int>();

            foreach (var instruction in _graph.Blocks.SelectMany(block => block.Instructions))
            {
                if (instruction.Destination is LocalVariable local)
                    counts[local] = counts.TryGetValue(local, out var count) ? count + 1 : 1;
            }

            return counts;
        }

        private void ReplaceLocalsUntilReassignment(Block startBlock, int startIndex, LocalVariable local,
            IOperand replacement, bool stopAtJoins)
        {
            var visited = new HashSet<Block>();
            var remaining = new Stack<(Block, int)>(_graph.Blocks.Count);

#if NET5_0_OR_GREATER
            visited.EnsureCapacity(_graph.Blocks.Count);
#endif

            visited.Add(startBlock);
            remaining.Push((startBlock, startIndex));

            while (remaining.Count > 0)
            {
                var (currentBlock, index) = remaining.Pop();

                // Process instructions starting at the given index
                for (var i = index; i < currentBlock.Instructions.Count; i++)
                {
                    var instruction = currentBlock.Instructions[i];

                    // A copied value is a snapshot: it cannot become a read of a later assignment
                    // to the source slot. Conservatively stop before either slot is reassigned.
                    if (instruction.Destination is LocalVariable destLocal &&
                        (destLocal == local || ReferenceEquals(destLocal, replacement)))
                        return;

                    // Replace operands
                    for (var j = 0; j < instruction.Operands.Count; j++)
                    {
                        var operand = instruction.Operands[j];

                        if (operand is LocalVariable usedLocal && usedLocal == local)
                        {
                            instruction.SetOperand(j, replacement);
                        }

                        // A memory operand's base/index holds an address, so only a local replacement may
                        // be substituted there (copy propagation). A constant/value replacement is left in
                        // place - the caller sees the local is still used and keeps its defining move.
                        else if (operand is MemoryOperand memory && replacement is LocalVariable)
                        {
                            if (memory.Base is LocalVariable baseLocal && baseLocal == local)
                                memory.Base = replacement;

                            if (memory.Index is LocalVariable indexLocal && indexLocal == local)
                                memory.Index = replacement;

                            instruction.SetOperand(j, memory);
                        }

                        // The object a field is accessed on is an address just like a memory base.
                        else if (operand is FieldReference field && replacement is LocalVariable fieldReplacement &&
                                 field.Local == local)
                        {
                            field.Local = fieldReplacement;
                        }
                    }
                }

                // Process successors
                foreach (var successor in currentBlock.Successors)
                {
                    // A join merges this local's other definitions, so for a non-SSA (multiply-defined)
                    // local the replacement must not flow past it - the value there is path-dependent.
                    if (stopAtJoins && successor.Predecessors.Count > 1)
                        continue;

                    if (visited.Add(successor))
                        remaining.Push((successor, 0));
                }
            }
        }

        private bool IsLocalUsedAfterInstruction(Block startBlock, int startIndex, LocalVariable local, out bool usedByMemory)
        {
            var visited = new HashSet<Block>();
            var remaining = new Stack<(Block, int)>(_graph.Blocks.Count);

#if NET5_0_OR_GREATER
            visited.EnsureCapacity(_graph.Blocks.Count);
#endif

            visited.Add(startBlock);
            remaining.Push((startBlock, startIndex));

            usedByMemory = false;

            while (remaining.Count > 0)
            {
                var (currentBlock, index) = remaining.Pop();

                // Process instructions
                for (var i = index; i < currentBlock.Instructions.Count; i++)
                {
                    var instruction = currentBlock.Instructions[i];
                    if (OperandEffects.ReadLocals(instruction).Contains(local))
                    {
                        // Nested operands read their receiver/address even in store destinations.
                        usedByMemory = instruction.Operands.Any(operand => operand is not LocalVariable
                            && OperandEffects.ReadLocals(operand).Contains(local));
                        return true;
                    }
                }

                // Process successors
                foreach (var successor in currentBlock.Successors)
                {
                    if (visited.Add(successor))
                        remaining.Push((successor, 0));
                }
            }

            return false;
        }
    }
}
