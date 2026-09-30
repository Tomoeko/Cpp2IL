using System;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Recovers literals which are being put into float fields/used as float constants,
/// converting them from their uint bit patterns into actual float/double literals.
/// </summary>
public static class FloatLiteralRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.OpCode == OpCode.Move && instruction.Operands is [FieldReference field, _])
                TryConvert(instruction, 1, field.Field.FieldType);
            else if (instruction.IsCall && instruction.Operands is [MethodAnalysisContext target, ..])
                ConvertArguments(instruction, target);
        }

        // Subnormal bit patterns need the retained native store proof. The
        // general field/call type hint alone does not establish their origin.
        X64NativeNullCheckedInvocationProof.TryNormalizeSubnormalFloatingStores(method);
    }

    private static void ConvertArguments(Instruction call, MethodAnalysisContext target)
    {
        var firstArgument = (call.OpCode == OpCode.Call ? 2 : 1) + (target.IsStatic ? 0 : 1);

        for (var i = 0; i < target.Parameters.Count; i++)
        {
            var index = firstArgument + i;

            if (index >= call.Operands.Count)
                break;

            TryConvert(call, index, target.Parameters[i].ParameterType);
        }
    }

    private static void TryConvert(Instruction instruction, int operandIndex, TypeAnalysisContext type)
    {
        if (!TryGetIntegerBits(instruction.Operands[operandIndex], out var bits))
            return;

        // TODO FIXME: We have to compare by name, not reference, because a field on a generic type resolves
        // TODO FIXME: to its own Single/Double context instance rather than the canonical one in SystemTypes.
        switch (type.FullName)
        {
            case "System.Single" when !IsSubnormal(bits, 32):
                instruction.SetOperand(operandIndex, new FloatLiteral(BitConverter.ToSingle(BitConverter.GetBytes((uint)bits), 0)));
                break;
            case "System.Double" when !IsSubnormal(bits, 64):
                instruction.SetOperand(operandIndex, new DoubleLiteral(BitConverter.ToDouble(BitConverter.GetBytes(bits), 0)));
                break;
        }
    }

    private static bool TryGetIntegerBits(IOperand operand, out ulong bits)
    {
        if (operand is Immediate immediate)
        {
            bits = immediate.UnsignedValue;
            return true;
        }

        bits = 0;
        return false;
    }

    // Subnormal source constants are valid. Their exponent is zero and their
    // mantissa is nonzero; zero itself does not require this additional proof.
    internal static bool IsSubnormal(ulong bits, int width) => width switch
    {
        32 => (bits & 0x7F800000u) == 0 && (bits & 0x007FFFFFu) != 0,
        64 => (bits & 0x7FF0000000000000UL) == 0 && (bits & 0x000FFFFFFFFFFFFFUL) != 0,
        _ => false,
    };
}
