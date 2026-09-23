using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>Conservative operand evaluation rules shared by elimination and propagation passes.</summary>
public static class OperandEffects
{
    /// <summary>
    /// Reading an already available value has no memory access, class initialization or bounds/null
    /// check. Moving a local read still requires separately proving that its value cannot change.
    /// Metadata contexts here are already resolved symbols, not loads of their native storage.
    /// </summary>
    public static bool IsPureValue(IOperand operand) => operand is LocalVariable or Register or Immediate
        or FloatLiteral or DoubleLiteral or StringLiteral or TypeAnalysisContext or MethodAnalysisContext
        or AddressOf { Target: LocalVariable };

    public static IEnumerable<LocalVariable> ReadLocals(Instruction instruction)
    {
        var destination = instruction.Destination as LocalVariable;
        var destinationIndex = instruction.OpCode is OpCode.Call or OpCode.IndirectCall ? 1 : 0;
        for (var index = 0; index < instruction.Operands.Count; index++)
        {
            var operand = instruction.Operands[index];
            if (index == destinationIndex && ReferenceEquals(operand, destination))
                continue;
            foreach (var local in ReadLocals(operand))
                yield return local;
        }
    }

    public static IEnumerable<LocalVariable> ReadLocals(IOperand operand)
    {
        switch (operand)
        {
            case LocalVariable local:
                yield return local;
                break;
            case MemoryOperand memory:
                if (memory.Base != null)
                    foreach (var read in ReadLocals(memory.Base))
                        yield return read;
                if (memory.Index != null)
                    foreach (var read in ReadLocals(memory.Index))
                        yield return read;
                break;
            case FieldReference { Field.IsStatic: false } field:
                yield return field.Local;
                break;
            case AddressOf address:
                foreach (var read in ReadLocals(address.Target))
                    yield return read;
                break;
            case ArrayAccess access:
                yield return access.Array;
                foreach (var read in ReadLocals(access.Index))
                    yield return read;
                break;
            case ArrayLength length:
                yield return length.Array;
                break;
        }
    }

    /// <summary>
    /// Slots that can change without a plain local assignment. Propagating copies of these slots
    /// would turn an earlier value snapshot into a later read after an alias or struct field write.
    /// </summary>
    public static HashSet<LocalVariable> LocalsWithMutableStorage(IEnumerable<Instruction> instructions)
    {
        var locals = new HashSet<LocalVariable>();
        foreach (var instruction in instructions)
        {
            if (instruction.Destination is FieldReference { Field.IsStatic: false } field
                && (field.Field.DeclaringType.IsValueType || field.Local.Type == null || field.Local.Type.IsValueType))
                locals.Add(field.Local);

            foreach (var operand in instruction.Operands)
                AddAddressedLocals(operand, locals);
        }
        return locals;
    }

    private static void AddAddressedLocals(IOperand operand, HashSet<LocalVariable> locals)
    {
        switch (operand)
        {
            case AddressOf address:
                // A target can itself be a value-type field or another composite storage location.
                // Protect all contributing locals when alias precision is unavailable.
                foreach (var local in ReadLocals(address.Target))
                    locals.Add(local);
                break;
            case MemoryOperand memory:
                if (memory.Base != null)
                    AddAddressedLocals(memory.Base, locals);
                if (memory.Index != null)
                    AddAddressedLocals(memory.Index, locals);
                break;
            case ArrayAccess access:
                AddAddressedLocals(access.Index, locals);
                break;
        }
    }
}
