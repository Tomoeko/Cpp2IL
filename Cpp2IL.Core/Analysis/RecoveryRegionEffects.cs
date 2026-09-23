using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Instructions that can disappear with an otherwise proven runtime-helper region. An unused
/// destination does not make memory evaluation or a potentially trapping operation harmless.
/// Pattern-specific memory effects require separate provenance; none are authorized here.
/// </summary>
internal static class RecoveryRegionEffects
{
    public static bool CanDiscard(Instruction instruction)
    {
        if (instruction.OpCode == OpCode.Nop)
            return instruction.Operands.Count == 0;
        if (instruction.OpCode == OpCode.Jump)
            return instruction.Operands is [Block];
        if (instruction.OpCode == OpCode.ConditionalJump)
            return instruction.Operands is [Block, var condition] && OperandEffects.IsPureValue(condition);

        var validShape = instruction.OpCode switch
        {
            OpCode.Move or OpCode.Not or OpCode.Negate => instruction.Operands.Count == 2,
            OpCode.Phi => instruction.Operands.Count >= 2,
            OpCode.Add or OpCode.Subtract or OpCode.Multiply
                or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned
                or OpCode.And or OpCode.Or or OpCode.Xor => instruction.Operands.Count == 3,
            var comparison when comparison.IsComparison() => instruction.Operands.Count == 3,
            // Signed and unsigned divide/remainder may trap even when their results are dead.
            _ => false,
        };
        return validShape && instruction.Operands[0] is LocalVariable
            && instruction.Operands.Skip(1).All(OperandEffects.IsPureValue);
    }
}
