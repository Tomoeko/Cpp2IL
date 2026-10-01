using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>A signed integer conversion rounded directly to its floating storage precision.</summary>
internal static class IntegerFloatConversion
{
    internal static bool TryGet(Instruction instruction) =>
        instruction.OpCode is OpCode.Int32ToSingle or OpCode.Int64ToDouble && instruction.IntegerBitWidth == 0 &&
        instruction.CallSemantics == CallSemantics.Direct &&
        instruction.Operands is [LocalVariable, LocalVariable];

    internal static bool HasCanonicalTypes(Instruction instruction, SystemTypesContext types) =>
        TryGet(instruction) && instruction.Operands is [LocalVariable result, LocalVariable source] &&
        ReferenceEquals(result.Type, ResultType(instruction, types)) &&
        ReferenceEquals(source.Type, SourceType(instruction, types));

    internal static TypeAnalysisContext SourceType(Instruction instruction, SystemTypesContext types) =>
        instruction.OpCode == OpCode.Int64ToDouble ? types.SystemInt64Type : types.SystemInt32Type;

    internal static TypeAnalysisContext ResultType(Instruction instruction, SystemTypesContext types) =>
        instruction.OpCode == OpCode.Int64ToDouble ? types.SystemDoubleType : types.SystemSingleType;
}
