using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

// Folds operations whose operands have become constant, and applies algebraic identities.
public static class ConstantFolder
{
    public static bool Run(MethodAnalysisContext method) => Run(method.ControlFlowGraph!);

    public static bool Run(ISILControlFlowGraph cfg)
    {
        var changed = false;

        foreach (var instruction in cfg.Instructions)
            changed |= TryFold(instruction);

        return changed;
    }

    private static bool TryFold(Instruction instruction)
    {
        if (instruction.IntegerBitWidth is not (0 or 32 or 64))
            return false;
        // Replacing native-width arithmetic with a move would erase its truncation/width contract.
        // Comparisons can fold because their result is always a normalized boolean.
        if (instruction.IntegerBitWidth != 0 && !instruction.OpCode.IsComparison())
            return false;

        // Widthless shifts cannot establish truncation or count masking. Keep them explicit
        // for the emitter to reject; native-width shifts retain their contract above.
        if (instruction.OpCode is OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned)
            return false;

        // Unary constant folds.
        if (instruction is { OpCode: OpCode.Not, Operands: [_, Immediate n] })
            return ToConstant(instruction, IsBoolean(instruction.Operands[0]) ? n.Value == 0 ? 1 : 0 : ~n.Value);
        if (instruction is { OpCode: OpCode.Negate, Operands: [_, Immediate m] })
            return ToConstant(instruction, -m.Value);

        // metadata handles are by definition never null
        if (instruction is { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, Operands: [_, TypeAnalysisContext, var against] }
            && Constant(against) == 0)
            return ToConstant(instruction, instruction.OpCode == OpCode.CheckEqual ? 0 : 1);

        // Binary constant folds.
        if (BinaryConstants(instruction, out var a, out var b))
        {
            if (instruction.IntegerBitWidth == 32)
            {
                a = unchecked((int)a);
                b = unchecked((int)b);
            }
            if (instruction.IntegerBitWidth is 32 or 64)
            {
                var unsignedA = instruction.IntegerBitWidth == 32 ? unchecked((uint)a) : unchecked((ulong)a);
                var unsignedB = instruction.IntegerBitWidth == 32 ? unchecked((uint)b) : unchecked((ulong)b);
                switch (instruction.OpCode)
                {
                    case OpCode.CheckLess: return ToConstant(instruction, a < b ? 1 : 0);
                    case OpCode.CheckGreater: return ToConstant(instruction, a > b ? 1 : 0);
                    case OpCode.CheckLessOrEqual: return ToConstant(instruction, a <= b ? 1 : 0);
                    case OpCode.CheckGreaterOrEqual: return ToConstant(instruction, a >= b ? 1 : 0);
                    case OpCode.CheckLessUnsigned: return ToConstant(instruction, unsignedA < unsignedB ? 1 : 0);
                    case OpCode.CheckGreaterUnsigned: return ToConstant(instruction, unsignedA > unsignedB ? 1 : 0);
                    case OpCode.CheckLessOrEqualUnsigned: return ToConstant(instruction, unsignedA <= unsignedB ? 1 : 0);
                    case OpCode.CheckGreaterOrEqualUnsigned: return ToConstant(instruction, unsignedA >= unsignedB ? 1 : 0);
                }
            }
            switch (instruction.OpCode)
            {
                case OpCode.CheckEqual: return ToConstant(instruction, a == b ? 1 : 0);
                case OpCode.CheckNotEqual: return ToConstant(instruction, a != b ? 1 : 0);
                case OpCode.And: return ToConstant(instruction, a & b);
                case OpCode.Or: return ToConstant(instruction, a | b);
                case OpCode.Xor: return ToConstant(instruction, a ^ b);
            }
        }

        // One-constant algebraic identities.
        switch (instruction.OpCode)
        {
            case OpCode.And:
                if (Constant(instruction.Operands[1]) == 0 || Constant(instruction.Operands[2]) == 0)
                    return ToConstant(instruction, 0);
                // x & 1 == x only when x is a 0/1 boolean
                return BooleanIdentity(instruction, 1) is { } andBool && ToMove(instruction, andBool);
            case OpCode.Or:
            case OpCode.Xor:
            case OpCode.Add:
                return Identity(instruction, 0);
            case OpCode.Multiply:
                return Identity(instruction, 1);
            case OpCode.Subtract:
                // Subtraction has only a right identity.
                return Constant(instruction.Operands[2]) == 0 && ToMove(instruction, instruction.Operands[1]);
        }

        return false;
    }

    // For a commutative op, if one operand is the identity constant, the result is the other operand.
    private static bool Identity(Instruction instruction, long identity)
    {
        if (Constant(instruction.Operands[1]) == identity)
            return ToMove(instruction, instruction.Operands[2]);
        if (Constant(instruction.Operands[2]) == identity)
            return ToMove(instruction, instruction.Operands[1]);
        return false;
    }

    // The boolean operand of `bool & const`, or null if that isn't the shape.
    private static IOperand? BooleanIdentity(Instruction instruction, long constant)
    {
        if (Constant(instruction.Operands[2]) == constant && IsBoolean(instruction.Operands[1]))
            return instruction.Operands[1];
        if (Constant(instruction.Operands[1]) == constant && IsBoolean(instruction.Operands[2]))
            return instruction.Operands[2];
        return null;
    }

    private static bool IsBoolean(IOperand operand) => operand is LocalVariable { Type.FullName: "System.Boolean" };

    private static bool BinaryConstants(Instruction instruction, out long left, out long right)
    {
        (left, right) = (0, 0);

        if (instruction.Operands is [_, Immediate a, Immediate b])
        {
            (left, right) = (a.Value, b.Value);
            return true;
        }

        return false;
    }

    private static long? Constant(IOperand operand) => operand is Immediate immediate ? immediate.Value : null;

    private static bool ToConstant(Instruction instruction, long value)
    {
        for (var index = 1; index < instruction.Operands.Count; index++)
            if (!OperandEffects.IsPureValue(instruction.Operands[index]))
                return false;

        instruction.OpCode = OpCode.Move;
        instruction.SetOperands(instruction.Operands[0], new Immediate(value));
        instruction.IntegerBitWidth = 0;
        return true;
    }

    private static bool ToMove(Instruction instruction, IOperand source)
    {
        instruction.OpCode = OpCode.Move;
        instruction.SetOperands(instruction.Operands[0], source);
        return true;
    }
}
