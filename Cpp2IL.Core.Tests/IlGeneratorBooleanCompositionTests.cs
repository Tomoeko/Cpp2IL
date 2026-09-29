using System;
using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(OpCode.And)]
    [TestCase(OpCode.Or)]
    [TestCase(OpCode.Xor)]
    public void BooleanCompositionTypesBranchConditionsAndExecutesEveryInput(OpCode opcode)
    {
        var types = _app.SystemTypes;
        var variants = new List<(string Name, bool? Constant, bool Swap)>();
        foreach (var (suffix, constant) in new (string, bool?)[] { ("Parameter", null), ("Zero", false), ("One", true) })
        foreach (var swap in new[] { false, true })
        {
            var name = suffix + (swap ? "Swapped" : "Direct");
            var (context, definition, parameters) = CreateMethod(name, types.SystemInt32Type,
                [types.SystemBooleanType, types.SystemBooleanType]);
            var condition = new LocalVariable("condition", new Register(930, "condition"));
            context.Locals.Add(condition);
            IOperand left = parameters[0];
            IOperand right = constant is { } value ? Imm(value ? 1 : 0) : parameters[1];
            if (swap)
                (left, right) = (right, left);
            var trueReturn = new Instruction(3, OpCode.Return, Imm(29));
            var operation = new Instruction(0, opcode, condition, left, right);
            context.ControlFlowGraph = new ISILControlFlowGraph([
                operation, new(1, OpCode.ConditionalJump, trueReturn, condition),
                new(2, OpCode.Return, Imm(11)), trueReturn,
            ]);
            LocalVariables.ResolveTypesAndFields(context);
            Assert.That(condition.Type, Is.SameAs(types.SystemBooleanType));
            Assert.That(operation.OpCode, Is.EqualTo(opcode));
            Assert.That(operation.IntegerBitWidth, Is.Zero);
            IlGenerator.GenerateIl(context, definition);
            variants.Add((name, constant, swap));
        }

        using var runtime = Load();
        foreach (var variant in variants)
        foreach (var left in new[] { false, true })
        foreach (var right in new[] { false, true })
        {
            var second = variant.Constant ?? right;
            var expected = CombineBooleans(opcode, left, second) ? 29 : 11;
            Assert.That(runtime.Type.GetMethod(variant.Name)!.Invoke(null, [left, right]), Is.EqualTo(expected));
        }
    }

    [TestCase(32, OpCode.And)]
    [TestCase(32, OpCode.Or)]
    [TestCase(32, OpCode.Xor)]
    [TestCase(64, OpCode.And)]
    [TestCase(64, OpCode.Or)]
    [TestCase(64, OpCode.Xor)]
    public void FloatingFlagCompositionPreservesAllOutcomesWithoutReturnTypeSeeding(int width, OpCode opcode)
    {
        var types = _app.SystemTypes;
        var floatingType = width == 32 ? types.SystemSingleType : types.SystemDoubleType;
        var (context, definition, parameters) = CreateMethod("FloatingBranch", types.SystemInt32Type,
            [floatingType, floatingType]);
        var carry = new LocalVariable("carry", new Register(931, "carry"));
        var zero = new LocalVariable("zero", new Register(932, "zero"));
        var condition = new LocalVariable("condition", new Register(933, "condition"));
        context.Locals.AddRange([carry, zero, condition]);
        var trueReturn = new Instruction(5, OpCode.Return, Imm(29));
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new(0, OpCode.FloatCompare, carry, parameters[0], parameters[1], Imm(width), Imm(9)),
            new(1, OpCode.FloatCompare, zero, parameters[0], parameters[1], Imm(width), Imm(10)),
            new(2, opcode, condition, carry, zero),
            new(3, OpCode.ConditionalJump, trueReturn, condition),
            new(4, OpCode.Return, Imm(11)), trueReturn,
        ]);
        LocalVariables.ResolveTypesAndFields(context);
        Assert.That(condition.Type, Is.SameAs(types.SystemBooleanType));
        FlagConditionRecovery.Run(context);
        SsaSimplifier.Run(context);
        ConstantFolder.Run(context);
        IlGenerator.GenerateIl(context, definition);

        using var runtime = Load();
        foreach (var left in FloatingValues(width))
        foreach (var right in FloatingValues(width))
        {
            var a = Convert.ToDouble(left);
            var b = Convert.ToDouble(right);
            var unordered = double.IsNaN(a) || double.IsNaN(b);
            var expected = CombineBooleans(opcode, unordered || a < b, unordered || a == b) ? 29 : 11;
            Assert.That(runtime.Type.GetMethod("FloatingBranch")!.Invoke(null, [left, right]), Is.EqualTo(expected));
        }
    }

    [TestCase("wide-operation")]
    [TestCase("narrow-operation")]
    [TestCase("nonboolean-constant")]
    [TestCase("negative-constant")]
    [TestCase("integer-local")]
    [TestCase("same-named-type")]
    [TestCase("memory")]
    [TestCase("constant-only")]
    public void BooleanCompositionCannotSupplyAnUnprovedManagedRepresentation(string defect)
    {
        var types = _app.SystemTypes;
        var (context, _, parameters) = CreateMethod("UnprovedBooleanComposition", types.SystemVoidType,
            [types.SystemBooleanType, types.SystemBooleanType]);
        var result = new LocalVariable("result", new Register(934, "result"));
        context.Locals.Add(result);
        IOperand left = parameters[0];
        IOperand right = parameters[1];
        switch (defect)
        {
            case "nonboolean-constant": right = Imm(2); break;
            case "negative-constant": right = Imm(-1); break;
            case "integer-local":
                right = new LocalVariable("integer", new Register(935, "integer"), types.SystemInt32Type);
                context.Locals.Add((LocalVariable)right);
                break;
            case "same-named-type":
                var impostor = new InjectedTypeAnalysisContext(_typeContext.DeclaringAssembly,
                    "System", "Boolean", types.SystemValueTypeType,
                    System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed);
                right = new LocalVariable("impostor", new Register(936, "impostor"), impostor);
                context.Locals.Add((LocalVariable)right);
                break;
            case "memory": right = new MemoryOperand(parameters[1]); break;
            case "constant-only": left = Imm(1); right = Imm(0); break;
        }
        var operation = new Instruction(0, OpCode.Xor, result, left, right)
        {
            IntegerBitWidth = defect switch { "wide-operation" => 64, "narrow-operation" => 8, _ => 0 },
        };
        context.ControlFlowGraph = new ISILControlFlowGraph([operation, new(1, OpCode.Return)]);
        LocalVariables.ResolveTypesAndFields(context);
        if (defect == "integer-local")
            Assert.That(result.Type, Is.SameAs(types.SystemInt32Type),
                "mixed operands retain the existing integer analysis instead of becoming Boolean");
        else
            Assert.That(result.Type, Is.Null);
    }

    private static bool CombineBooleans(OpCode opcode, bool left, bool right) => opcode switch
    {
        OpCode.And => left & right,
        OpCode.Or => left | right,
        OpCode.Xor => left ^ right,
        _ => throw new ArgumentOutOfRangeException(nameof(opcode)),
    };
}
