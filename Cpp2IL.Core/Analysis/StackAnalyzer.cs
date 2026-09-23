using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

public class StackAnalyzer
{
    [DebuggerDisplay("Size = {Size}")]
    private class StackState
    {
        public int Size;
        public StackState Copy() => new() { Size = this.Size };
    }

    private Dictionary<Block, StackState> _inComingState = [];
    private Dictionary<Block, StackState> _outGoingState = [];
    private Dictionary<Instruction, StackState> _instructionState = [];

    /// <summary>
    /// Max allowed count of blocks to visit (-1 for no limit).
    /// </summary>
    public static int MaxBlockVisitCount = 500000; //High enough to not be legitimately hit, but still give up if something loops infinitely.

    public static void Analyze(MethodAnalysisContext method)
    {
        var analyzer = new StackAnalyzer();

        var graph = method.ControlFlowGraph!;
        graph.RemoveUnreachableBlocks(); // Without this indirect jumps (in try catch i think) cause some weird stuff

        analyzer._inComingState = new Dictionary<Block, StackState> { { graph.EntryBlock, new StackState() } };

        analyzer.TraverseGraph(graph.EntryBlock, graph.ExitBlock);

        // Exit is a graph sentinel, not a native join: normal returns restore the frame,
        // while a proved throw unwinds it. Inspect each path without merging their deltas.
        foreach (var predecessor in graph.ExitBlock.Predecessors)
        {
            if (predecessor.Instructions.LastOrDefault()?.OpCode == OpCode.Throw ||
                !analyzer._outGoingState.TryGetValue(predecessor, out var outDelta) || outDelta.Size == 0)
                continue;
            var outText = outDelta.Size < 0 ? "-" + (-(long)outDelta.Size).ToString("X") : outDelta.Size.ToString("X");
            method.AddWarning($"Method ends with non empty stack ({outText}) on native exit block {predecessor.ID}; the output could be wrong!");
        }

        analyzer.ResolveFrameAliases(graph);
        analyzer.CorrectOffsets(graph);
        ReplaceStackWithRegisters(method);

        graph.RemoveNops();
        graph.RemoveEmptyBlocks();
    }

    // consider mov [reg], [stack pointer]
    // now we need to handle [reg] as if it were a stack pointer, forever.
    private void ResolveFrameAliases(ISILControlFlowGraph graph)
    {
        var aliases = new Dictionary<string, int>();

        foreach (var instruction in graph.EntryBlock.Successors.SelectMany(b => b.Instructions))
        {
            if (instruction is { OpCode: OpCode.Move, Operands: [Register destination, Register { Name: "rsp" }] }
                && _instructionState.TryGetValue(instruction, out var atCopy))
                aliases[destination.Name] = atCopy.Size;
        }

        if (aliases.Count == 0)
            return;

        // Following a register that gets reassigned would need flow analysis, so stop trusting it entirely
        foreach (var instruction in graph.Instructions)
        {
            if (instruction is { OpCode: OpCode.Move, Operands: [Register, Register { Name: "rsp" }] })
                continue;

            if (instruction.Destination is Register written)
                aliases.Remove(written.Name);
        }

        foreach (var instruction in graph.Instructions)
        {
            if (!_instructionState.TryGetValue(instruction, out var state))
                continue;

            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not MemoryOperand { Index: null, Scale: 0, Base: Register frameBase } memory)
                    continue;

                if (!aliases.TryGetValue(frameBase.Name, out var frameOffset))
                    continue;

                instruction.SetOperand(i, new StackOffset(checked((int)(frameOffset + memory.Addend - state.Size))));
            }
        }
    }

    private void CorrectOffsets(ISILControlFlowGraph graph)
    {
        foreach (var block in graph.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is { OpCode: OpCode.ShiftStack })
                {
                    // Nop the shift stack instruction
                    instruction.OpCode = OpCode.Nop;
                    instruction.SetOperands();
                    continue;
                }

                int? state = null;

                // Correct offset for stack operands.
                for (var i = 0; i < instruction.Operands.Count; i++)
                {
                    var op = instruction.Operands[i];

                    var slot = op switch
                    {
                        StackOffset direct => direct,
                        AddressOf { Target: StackOffset addressed } => addressed,
                        _ => (StackOffset?)null
                    };

                    if (slot is { } offset)
                    {
                        // This can only be done before modifying any of the instruction operands,
                        // as doing so will make the dictionary lookup impossible.
                        state ??= _instructionState[instruction].Size;

                        var actual = new StackOffset(checked(state.Value + offset.Offset));
                        instruction.SetOperand(i, op is AddressOf ? new AddressOf(actual) : actual);
                    }
                }
            }
        }
    }

    // Traverse the graph and calculate the stack state for each block and instruction
    private void TraverseGraph(Block initialBlock, Block exitBlock, int initialVisitedBlockCount = 0)
    {
        var blockLevelState = new Stack<(Block, int)>();
        blockLevelState.Push((initialBlock, initialVisitedBlockCount));

        while (blockLevelState.Count > 0)
        {
            var (block, visitedBlockCount) = blockLevelState.Pop();

            // Copy current state
            var incomingState = _inComingState[block];
            var currentState = incomingState.Copy();

            // Process instructions
            foreach (var instruction in block.Instructions)
            {
                _instructionState[instruction] = currentState;

                if (instruction.OpCode == OpCode.ShiftStack)
                {
                    var offset = checked((int)((Immediate)instruction.Operands[0]).Value);
                    currentState = currentState.Copy();
                    currentState.Size = checked(currentState.Size + offset);
                }
            }

            _outGoingState[block] = currentState;

            visitedBlockCount++;

            if (MaxBlockVisitCount != -1 && visitedBlockCount > MaxBlockVisitCount)
                throw new DecompilerException($"Stack state not settling! ({visitedBlockCount} blocks already visited)");

            // Visit successors
            foreach (var successor in block.Successors)
            {
                if (ReferenceEquals(successor, exitBlock))
                    continue;
                // A real native join needs one stack position. Replacing one predecessor's
                // delta with another silently aliases distinct slots and can oscillate in loops.
                if (_inComingState.TryGetValue(successor, out var existingState))
                {
                    if (existingState.Size != currentState.Size)
                        throw new DecompilerException("Native stack delta differs at a control-flow join");
                }
                else
                {
                    // Set incoming delta and add to queue
                    _inComingState[successor] = currentState.Copy();
                    blockLevelState.Push((successor, visitedBlockCount + 1));
                }
            }
        }
    }

    private static void ReplaceStackWithRegisters(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;

        // Replace stack offset operands
        foreach (var instruction in instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                if (operand is StackOffset offset)
                    instruction.SetOperand(i, new Register(null, NameForSlot(offset)));

                if (operand is AddressOf { Target: StackOffset addressed })
                    instruction.SetOperand(i, new AddressOf(new Register(null, NameForSlot(addressed))));
            }
        }

        // Replace params
        for (var i = 0; i < method.ParameterOperands.Count; i++)
        {
            var parameter = method.ParameterOperands[i];

            if (parameter is StackOffset offset)
                method.ParameterOperands[i] = new Register(null, NameForSlot(offset));
        }
    }

    private static string NameForSlot(StackOffset offset) => offset.Offset < 0 ? $"stack_-{-(long)offset.Offset:X}" : $"stack_{offset.Offset:X}";
}
