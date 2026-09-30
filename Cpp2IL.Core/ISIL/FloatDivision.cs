using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>A scalar division with an explicit IEEE storage precision.</summary>
internal readonly record struct FloatDivision(int Width)
{
    internal static bool TryGet(Instruction instruction, out FloatDivision division)
    {
        division = default;
        if (instruction.OpCode != OpCode.FloatDivide || instruction.IntegerBitWidth != 0 ||
            instruction.CallSemantics != CallSemantics.Direct || instruction.Operands is not
                [LocalVariable, LocalVariable, LocalVariable, Immediate { Value: 32 or 64 } width])
            return false;
        division = new((int)width.Value);
        return true;
    }

    internal TypeAnalysisContext ResultType(SystemTypesContext types) =>
        Width == 32 ? types.SystemSingleType : types.SystemDoubleType;
}
