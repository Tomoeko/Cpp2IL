using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>A signed Int32 conversion rounded directly to binary32.</summary>
internal static class IntegerFloatConversion
{
    internal static bool TryGet(Instruction instruction) =>
        instruction.OpCode == OpCode.Int32ToSingle && instruction.IntegerBitWidth == 0 &&
        instruction.CallSemantics == CallSemantics.Direct &&
        instruction.Operands is [LocalVariable, LocalVariable];

    internal static bool HasCanonicalTypes(Instruction instruction, SystemTypesContext types) =>
        TryGet(instruction) && instruction.Operands is [LocalVariable result, LocalVariable source] &&
        ReferenceEquals(result.Type, types.SystemSingleType) &&
        ReferenceEquals(source.Type, types.SystemInt32Type);
}
