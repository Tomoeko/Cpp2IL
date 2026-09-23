using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>Scalar binary64 truncation to a signed integer, with MIN for NaN or overflow.</summary>
internal readonly record struct FloatTruncation(int ResultBits)
{
    public static bool TryGet(Instruction instruction, out FloatTruncation conversion)
    {
        conversion = default;
        if (instruction.OpCode != OpCode.FloatTruncateSigned || instruction.IntegerBitWidth != 0 ||
            instruction.Operands is not [LocalVariable, LocalVariable,
                Immediate { Value: 64 }, Immediate { Value: 32 or 64 } width])
            return false;
        conversion = new((int)width.Value);
        return true;
    }

    public TypeAnalysisContext ResultType(SystemTypesContext types) =>
        ResultBits == 32 ? types.SystemInt32Type : types.SystemInt64Type;

    public bool HasCanonicalTypes(Instruction instruction, SystemTypesContext types) =>
        instruction.Operands is [LocalVariable destination, LocalVariable source, _, _] &&
        ReferenceEquals(source.Type, types.SystemDoubleType) &&
        ReferenceEquals(destination.Type, ResultType(types));
}
