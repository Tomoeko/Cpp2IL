using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>A scalar addition or subtraction rounded to an explicit IEEE storage precision.</summary>
internal readonly record struct FloatAddSubtract(int Width, bool Subtract)
{
    internal static bool TryGet(Instruction instruction, out FloatAddSubtract arithmetic)
    {
        arithmetic = default;
        if (instruction.OpCode is not (OpCode.FloatAdd or OpCode.FloatSubtract) ||
            instruction.IntegerBitWidth != 0 || instruction.CallSemantics != CallSemantics.Direct ||
            instruction.Operands is not [LocalVariable, LocalVariable, LocalVariable, Immediate { Value: 32 or 64 } width])
            return false;
        arithmetic = new((int)width.Value, instruction.OpCode == OpCode.FloatSubtract);
        return true;
    }

    internal TypeAnalysisContext ResultType(SystemTypesContext types) =>
        Width == 32 ? types.SystemSingleType : types.SystemDoubleType;
}
