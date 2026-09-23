using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Recovers comparisons from complete, matching SSA flag definitions. Signed conditions require
/// both SF and OF from the same subtraction; unsigned conditions use CF and ZF. SF alone tests
/// the wrapped subtraction result and cannot be replaced by a comparison of its inputs.
/// </summary>
public static class FlagConditionRecovery
{
    private readonly record struct Comparison(OpCode Opcode, IOperand Left, IOperand Right, int Width)
    {
        public bool SameInputs(Comparison other) => Equals(Left, other.Left) && Equals(Right, other.Right) && Width == other.Width;
    }

    public static void Run(MethodAnalysisContext method) => Run(method.ControlFlowGraph!);

    public static void Run(ISILControlFlowGraph cfg)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        var ambiguous = new HashSet<LocalVariable>();
        foreach (var instruction in cfg.Blocks.SelectMany(b => b.Instructions))
        {
            if (instruction.Destination is not LocalVariable local)
                continue;
            if (definitions.ContainsKey(local))
                ambiguous.Add(local);
            else
                definitions.Add(local, instruction);
        }
        foreach (var local in ambiguous)
            definitions.Remove(local);

        // Classify against an unchanged graph: an early rewrite of ZF must not invalidate a later
        // SF/OF/ZF proof. This also covers setcc results which never feed a conditional branch.
        var rewrites = new List<(Instruction Instruction, Comparison Comparison)>();
        foreach (var pair in definitions)
        {
            if (TryClassify(pair.Key, definitions, new HashSet<LocalVariable>(), out var comparison))
                rewrites.Add((pair.Value, comparison));
        }
        foreach (var (instruction, comparison) in rewrites)
        {
            instruction.OpCode = comparison.Opcode;
            instruction.SetOperands(instruction.Operands[0], comparison.Left, comparison.Right);
            instruction.IntegerBitWidth = comparison.Width;
        }
    }

    private static bool TryClassify(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions,
        HashSet<LocalVariable> visiting, out Comparison comparison)
    {
        comparison = default;
        if (!definitions.TryGetValue(local, out var definition) || !visiting.Add(local))
            return false;
        try
        {
            if (TryZeroFlag(local, definitions, out comparison))
                return true;
            if (TrySignedFlags(local, definitions, out comparison))
                return true;
            if (definition.OpCode.IsUnsignedComparison() && definition.Operands.Count == 3)
            {
                comparison = new(definition.OpCode, definition.Operands[1], definition.Operands[2], definition.IntegerBitWidth);
                return true;
            }
            if (definition.Operands is [_, LocalVariable source] && definition.OpCode is (OpCode.Move or OpCode.Not) &&
                TryClassify(source, definitions, visiting, out comparison))
            {
                if (definition.OpCode == OpCode.Not)
                    comparison = comparison with { Opcode = Invert(comparison.Opcode) };
                return true;
            }
            if (definition.OpCode == OpCode.CheckEqual && definition.Operands is [_, LocalVariable tested, Immediate { Value: 0 }] &&
                TryClassify(tested, definitions, visiting, out comparison))
            {
                comparison = comparison with { Opcode = Invert(comparison.Opcode) };
                return true;
            }
            if (definition.OpCode is not (OpCode.And or OpCode.Or) ||
                definition.Operands is not [_, LocalVariable left, LocalVariable right] ||
                !TryClassify(left, definitions, visiting, out var leftComparison) ||
                !TryClassify(right, definitions, visiting, out var rightComparison) ||
                !leftComparison.SameInputs(rightComparison))
                return false;

            if (definition.OpCode == OpCode.And)
            {
                if (rightComparison.Opcode == OpCode.CheckNotEqual)
                    comparison = leftComparison;
                else if (leftComparison.Opcode == OpCode.CheckNotEqual)
                    comparison = rightComparison;
                else
                    return false;
                comparison = comparison with
                {
                    Opcode = comparison.Opcode switch
                    {
                        OpCode.CheckGreaterOrEqual => OpCode.CheckGreater,
                        OpCode.CheckGreaterOrEqualUnsigned => OpCode.CheckGreaterUnsigned,
                        _ => default,
                    }
                };
            }
            else
            {
                if (rightComparison.Opcode == OpCode.CheckEqual)
                    comparison = leftComparison;
                else if (leftComparison.Opcode == OpCode.CheckEqual)
                    comparison = rightComparison;
                else
                    return false;
                comparison = comparison with
                {
                    Opcode = comparison.Opcode switch
                    {
                        OpCode.CheckLess => OpCode.CheckLessOrEqual,
                        OpCode.CheckLessUnsigned => OpCode.CheckLessOrEqualUnsigned,
                        _ => default,
                    }
                };
            }
            return comparison.Opcode != default;
        }
        finally
        {
            visiting.Remove(local);
        }
    }

    private static OpCode Invert(OpCode opcode) => opcode switch
    {
        OpCode.CheckEqual => OpCode.CheckNotEqual,
        OpCode.CheckNotEqual => OpCode.CheckEqual,
        OpCode.CheckLess => OpCode.CheckGreaterOrEqual,
        OpCode.CheckGreaterOrEqual => OpCode.CheckLess,
        OpCode.CheckGreater => OpCode.CheckLessOrEqual,
        OpCode.CheckLessOrEqual => OpCode.CheckGreater,
        OpCode.CheckLessUnsigned => OpCode.CheckGreaterOrEqualUnsigned,
        OpCode.CheckGreaterOrEqualUnsigned => OpCode.CheckLessUnsigned,
        OpCode.CheckGreaterUnsigned => OpCode.CheckLessOrEqualUnsigned,
        OpCode.CheckLessOrEqualUnsigned => OpCode.CheckGreaterUnsigned,
        _ => throw new ArgumentOutOfRangeException(nameof(opcode)),
    };

    private static bool TryZeroFlag(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions, out Comparison comparison)
    {
        comparison = default;
        if (Get(local, definitions) is not { OpCode: OpCode.CheckEqual, Operands: [_, LocalVariable difference, Immediate { Value: 0 }] } flag ||
            Get(difference, definitions) is not { OpCode: OpCode.Subtract, Operands: [_, var left, var right] } subtraction ||
            flag.IntegerBitWidth != subtraction.IntegerBitWidth)
            return false;
        comparison = new(OpCode.CheckEqual, left, right, subtraction.IntegerBitWidth);
        return true;
    }

    private static bool TrySignedFlags(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions, out Comparison comparison)
    {
        comparison = default;
        if (Get(local, definitions) is not { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, Operands: [_, LocalVariable first, LocalVariable second] } condition)
            return false;
        if (!TrySignAndOverflow(first, second, definitions, out comparison) && !TrySignAndOverflow(second, first, definitions, out comparison))
            return false;
        comparison = comparison with { Opcode = condition.OpCode == OpCode.CheckEqual ? OpCode.CheckGreaterOrEqual : OpCode.CheckLess };
        return true;
    }

    private static bool TrySignAndOverflow(LocalVariable sign, LocalVariable overflow, Dictionary<LocalVariable, Instruction> definitions, out Comparison comparison)
    {
        comparison = default;
        if (Get(sign, definitions) is not { OpCode: OpCode.CheckLess, Operands: [_, LocalVariable difference, Immediate { Value: 0 }] } signDefinition ||
            Get(difference, definitions) is not { OpCode: OpCode.Subtract, Operands: [_, var left, var right] } subtraction ||
            Get(overflow, definitions) is not { OpCode: OpCode.CheckLess, Operands: [_, LocalVariable bits, Immediate { Value: 0 }] } overflowDefinition ||
            Get(bits, definitions) is not { OpCode: OpCode.And, Operands: [_, LocalVariable first, LocalVariable second] } and)
            return false;
        var width = subtraction.IntegerBitWidth;
        if (signDefinition.IntegerBitWidth != width || overflowDefinition.IntegerBitWidth != width || and.IntegerBitWidth != width)
            return false;
        if (!(IsXor(first, left, right, width, definitions) && IsXor(second, left, difference, width, definitions)) &&
            !(IsXor(second, left, right, width, definitions) && IsXor(first, left, difference, width, definitions)))
            return false;
        comparison = new(OpCode.CheckGreaterOrEqual, left, right, width);
        return true;
    }

    private static bool IsXor(LocalVariable local, IOperand left, IOperand right, int width, Dictionary<LocalVariable, Instruction> definitions) =>
        Get(local, definitions) is { OpCode: OpCode.Xor, Operands: [_, var first, var second] } xor && xor.IntegerBitWidth == width &&
        (Equals(first, left) && Equals(second, right) || Equals(first, right) && Equals(second, left));

    private static Instruction? Get(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions) =>
        definitions.TryGetValue(local, out var definition) ? definition : null;
}
