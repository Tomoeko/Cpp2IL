using System.Collections.Generic;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Removes pure instructions whose result is never used. This eliminates, among other things, the
/// dead flag/temporary computations the x86 lifter emits eagerly for every comparison - a single
/// <c>cmp</c>/<c>test</c> produces all of CF/OF/SF/ZF/PF plus scratch temporaries, but the branch
/// that follows only consumes one of them.
///
/// Uses global read counts. In SSA, a zero count identifies one dead definition; after SSA removal,
/// a read conservatively keeps every definition of that local. Instructions are turned into nops
/// rather than spliced out so that control-flow and phi structure remain intact.
/// </summary>
public static class DeadCodeEliminator
{
    public static void Run(MethodAnalysisContext method) => Run(method.ControlFlowGraph!);

    public static void Run(ISILControlFlowGraph cfg)
    {
        // Removing a dead definition can make its operands dead in turn, so iterate to a fixpoint.
        // This is monotonic (each pass only nops instructions) and therefore always terminates.
        var changed = true;
        while (changed)
        {
            changed = false;

            var useCounts = CountUses(cfg);

            foreach (var block in cfg.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (!IsRemovable(instruction))
                        continue;

                    // Only definitions of a register local are candidates. Stores have a memory or
                    // field destination (Destination is not a local) and are never dead.
                    if (instruction.Destination is not LocalVariable destination)
                        continue;

                    if (useCounts.TryGetValue(destination, out var count) && count > 0)
                        continue;

                    instruction.OpCode = OpCode.Nop;
                    instruction.SetOperands();
                    changed = true;
                }
            }
        }
    }

    private static Dictionary<LocalVariable, int> CountUses(ISILControlFlowGraph cfg)
    {
        var counts = new Dictionary<LocalVariable, int>();

        foreach (var block in cfg.Blocks)
            foreach (var instruction in block.Instructions)
                foreach (var used in OperandEffects.ReadLocals(instruction))
                    counts[used] = counts.TryGetValue(used, out var c) ? c + 1 : 1;

        return counts;
    }

    /// <summary>
    /// Removing an unused result must preserve exceptions and initialization side effects too.
    /// A normally pure opcode may still evaluate a memory/field/array operand that can throw.
    /// </summary>
    private static bool IsRemovable(Instruction instruction)
    {
        var pureOperation = instruction.OpCode switch
        {
            OpCode.Move or OpCode.Phi or OpCode.UnresolvedValue
                or OpCode.Add or OpCode.Subtract or OpCode.Multiply
                or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned
                or OpCode.And or OpCode.Or or OpCode.Xor
                or OpCode.Not or OpCode.Negate => true,
            var comparison when comparison.IsComparison() => true,
            _ => false
        };
        if (!pureOperation)
            return false;

        // These opcodes define operand zero. Be conservative about all other operand kinds:
        // instance reads can fault, static reads can initialize a class, and even taking an
        // array element's address can throw. Divide/remainder also remain until proven safe.
        for (var index = 1; index < instruction.Operands.Count; index++)
        {
            if (!OperandEffects.IsPureValue(instruction.Operands[index]))
                return false;
        }

        return true;
    }
}
