using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>A numeric conversion between the binary32 and binary64 storage formats.</summary>
internal readonly record struct FloatConversion(int SourceWidth, int ResultWidth)
{
    internal static bool TryGet(Instruction instruction, out FloatConversion conversion)
    {
        conversion = default;
        if (instruction.OpCode != OpCode.FloatConvert || instruction.IntegerBitWidth != 0 ||
            instruction.CallSemantics != CallSemantics.Direct || instruction.Operands is not
                [LocalVariable, LocalVariable, Immediate { Value: 32 or 64 } source,
                    Immediate { Value: 32 or 64 } result] || source.Value == result.Value)
            return false;
        conversion = new((int)source.Value, (int)result.Value);
        return true;
    }

    internal TypeAnalysisContext SourceType(SystemTypesContext types) =>
        SourceWidth == 32 ? types.SystemSingleType : types.SystemDoubleType;

    internal TypeAnalysisContext ResultType(SystemTypesContext types) =>
        ResultWidth == 32 ? types.SystemSingleType : types.SystemDoubleType;
}
