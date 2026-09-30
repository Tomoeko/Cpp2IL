using System;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

internal readonly record struct FloatProjection(int Width)
{
    public static bool TryGet(Instruction instruction, out FloatProjection projection)
    {
        projection = default;
        if (instruction.OpCode != OpCode.FloatProject || instruction.IntegerBitWidth != 0 ||
            instruction.Operands is not [LocalVariable, var source, Immediate { Value: 32 or 64 } width] ||
            !(source is LocalVariable || width.Value == 32 && source is FloatLiteral single &&
              BitConverter.DoubleToInt64Bits(single.Value) == 0 ||
              width.Value == 64 && source is DoubleLiteral value && BitConverter.DoubleToInt64Bits(value.Value) == 0))
            return false;
        projection = new((int)width.Value);
        return true;
    }

    public TypeAnalysisContext ResultType(SystemTypesContext types) =>
        Width == 32 ? types.SystemSingleType : types.SystemDoubleType;
}
