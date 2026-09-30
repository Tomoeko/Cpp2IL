using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>Negates an ordered negative scalar; preserves both zero signs and unordered NaNs.</summary>
internal readonly record struct FloatNegativeSelection(int Width)
{
    internal static bool TryGet(Instruction instruction, out FloatNegativeSelection selection)
    {
        selection = default;
        if (instruction.OpCode != OpCode.FloatNegateNegative || instruction.IntegerBitWidth != 0 ||
            instruction.CallSemantics != CallSemantics.Direct || instruction.Operands is not
                [LocalVariable, LocalVariable, Immediate { Value: 32 or 64 } width])
            return false;
        selection = new((int)width.Value);
        return true;
    }

    internal TypeAnalysisContext ResultType(SystemTypesContext types) =>
        Width == 32 ? types.SystemSingleType : types.SystemDoubleType;
}
