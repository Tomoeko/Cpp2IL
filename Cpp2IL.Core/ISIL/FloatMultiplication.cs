using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>A scalar multiplication rounded to an explicit IEEE storage precision.</summary>
internal readonly record struct FloatMultiplication(int Width)
{
    internal static bool TryGet(Instruction instruction, out FloatMultiplication multiplication)
    {
        multiplication = default;
        if (instruction.OpCode != OpCode.FloatMultiply || instruction.IntegerBitWidth != 0 ||
            instruction.CallSemantics != CallSemantics.Direct || instruction.Operands is not
                [LocalVariable, var left, var right, Immediate { Value: 32 or 64 } width] ||
            !TypedOperand(left, (int)width.Value) || !TypedOperand(right, (int)width.Value))
            return false;
        multiplication = new((int)width.Value);
        return true;
    }

    internal TypeAnalysisContext ResultType(SystemTypesContext types) =>
        Width == 32 ? types.SystemSingleType : types.SystemDoubleType;

    private static bool TypedOperand(IOperand operand, int width) => operand is LocalVariable ||
        width == 32 && operand is FloatLiteral || width == 64 && operand is DoubleLiteral;
}
