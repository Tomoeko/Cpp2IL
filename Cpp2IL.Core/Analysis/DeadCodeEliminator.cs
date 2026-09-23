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
                foreach (var used in UsedLocals(instruction))
                    counts[used] = counts.TryGetValue(used, out var c) ? c + 1 : 1;

        return counts;
    }

    /// <summary>
    /// Every local read by the instruction. The single write position - a plain local destination -
    /// is excluded. Memory and field operands always contribute their address/object locals as
    /// reads, even when they are the destination of a store.
    /// </summary>
    private static IEnumerable<LocalVariable> UsedLocals(Instruction instruction)
    {
        var destination = instruction.Destination as LocalVariable;
        var destinationIndex = instruction.OpCode is OpCode.Call or OpCode.IndirectCall ? 1 : 0;

        for (var index = 0; index < instruction.Operands.Count; index++)
        {
            var operand = instruction.Operands[index];
            if (index == destinationIndex && ReferenceEquals(operand, destination))
                continue;

            switch (operand)
            {
                case LocalVariable local:
                    yield return local;
                    break;
                case MemoryOperand memory:
                    if (memory.Base is LocalVariable baseLocal)
                        yield return baseLocal;
                    if (memory.Index is LocalVariable indexLocal)
                        yield return indexLocal;
                    break;
                // A static field access doesn't read the storage pointer it was resolved from, so that
                // pointer (and the class load feeding it) is free to die.
                case FieldReference { Field.IsStatic: false, Local: { } fieldLocal }:
                    yield return fieldLocal;
                    break;
                // Handing out a slot's address is a read of it as far as we can tell, whatever the callee then does with it.
                case AddressOf { Target: LocalVariable addressed }:
                    yield return addressed;
                    break;
                case AddressOf { Target: ArrayAccess addressedElement }:
                    foreach (var used in ArrayAccessLocals(addressedElement))
                        yield return used;
                    break;
                case ArrayAccess access:
                    foreach (var used in ArrayAccessLocals(access))
                        yield return used;
                    break;
                case ArrayLength { Array: { } lengthArray }:
                    yield return lengthArray;
                    break;
            }
        }
    }

    private static IEnumerable<LocalVariable> ArrayAccessLocals(ArrayAccess access)
    {
        yield return access.Array;

        if (access.Index is LocalVariable index)
            yield return index;
    }

    /// <summary>
    /// Removing an unused result must preserve exceptions and initialization side effects too.
    /// A normally pure opcode may still evaluate a memory/field/array operand that can throw.
    /// </summary>
    private static bool IsRemovable(Instruction instruction)
    {
        var pureOperation = instruction.OpCode switch
        {
            OpCode.Move or OpCode.Phi
                or OpCode.Add or OpCode.Subtract or OpCode.Multiply
                or OpCode.ShiftLeft or OpCode.ShiftRight
                or OpCode.And or OpCode.Or or OpCode.Xor
                or OpCode.Not or OpCode.Negate => true,
            >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqual => true,
            _ => false
        };
        if (!pureOperation)
            return false;

        // These opcodes define operand zero. Be conservative about all other operand kinds:
        // instance reads can fault, static reads can initialize a class, and even taking an
        // array element's address can throw. Divide/remainder also remain until proven safe.
        for (var index = 1; index < instruction.Operands.Count; index++)
        {
            if (instruction.Operands[index] is not (LocalVariable or Register or Immediate
                or FloatLiteral or DoubleLiteral or StringLiteral or AddressOf { Target: LocalVariable }))
                return false;
        }

        return true;
    }
}
