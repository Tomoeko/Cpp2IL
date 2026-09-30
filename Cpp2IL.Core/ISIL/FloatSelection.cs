using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

internal readonly record struct FloatSelection(int Width, bool Maximum)
{
    public static bool TryGet(Instruction instruction, out FloatSelection selection)
    {
        selection = default;
        if (instruction.OpCode != OpCode.FloatSelect || instruction.IntegerBitWidth != 0 ||
            instruction.Operands is not [LocalVariable, LocalVariable, LocalVariable,
                Immediate { Value: 32 or 64 } width, Immediate { Value: 0 or 1 } maximum])
            return false;
        selection = new((int)width.Value, maximum.Value == 1);
        return true;
    }

    public TypeAnalysisContext ResultType(SystemTypesContext types) =>
        Width == 32 ? types.SystemSingleType : types.SystemDoubleType;
}
