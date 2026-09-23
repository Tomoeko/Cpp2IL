using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(32, false)]
    [TestCase(64, false)]
    [TestCase(32, true)]
    [TestCase(64, true)]
    public void FloatingComparisonExecutesEveryOutcomeMask(int width, bool copyToLocals)
    {
        var type = width == 32 ? _app.SystemTypes.SystemSingleType : _app.SystemTypes.SystemDoubleType;
        for (var mask = 0; mask < 16; mask++)
        {
            var (context, definition, parameters) = CreateMethod("FloatMask" + mask, _app.SystemTypes.SystemBooleanType, [type, type]);
            var left = parameters[0];
            var right = parameters[1];
            List<Instruction> code = [];
            if (copyToLocals)
            {
                left = new LocalVariable("left", new Register(700, "left"), type);
                right = new LocalVariable("right", new Register(701, "right"), type);
                context.Locals.Add(left);
                context.Locals.Add(right);
                code.Add(new(code.Count, OpCode.Move, left, parameters[0]));
                code.Add(new(code.Count, OpCode.Move, right, parameters[1]));
            }
            var result = new LocalVariable("result", new Register(702, "result"), _app.SystemTypes.SystemBooleanType);
            context.Locals.Add(result);
            var compare = new Instruction(code.Count, OpCode.FloatCompare, result, left, right, Imm(width), Imm(mask));
            code.Add(compare);
            code.Add(new(code.Count, OpCode.Return, result));
            var graph = new ISILControlFlowGraph(code);
            FlagConditionRecovery.Run(graph);
            ConstantFolder.Run(graph);
            DeadCodeEliminator.Run(graph);
            Emit(context, definition, graph.Instructions);
        }

        using var runtime = Load();
        var values = FloatingValues(width);
        for (var mask = 0; mask < 16; mask++)
        {
            var method = runtime.Type.GetMethod("FloatMask" + mask)!;
            foreach (var left in values)
            foreach (var right in values)
            {
                var a = Convert.ToDouble(left);
                var b = Convert.ToDouble(right);
                var outcome = double.IsNaN(a) || double.IsNaN(b) ? 8 : a < b ? 1 : a > b ? 4 : 2;
                Assert.That(method.Invoke(null, [left, right]), Is.EqualTo((mask & outcome) != 0),
                    $"width={width}, mask={mask}, left={a:R}, right={b:R}");
            }
        }
    }

    [TestCase(32)]
    [TestCase(64)]
    public void FloatingPredicateNegationSurvivesIntegerFlagRecovery(int width)
    {
        var type = width == 32 ? _app.SystemTypes.SystemSingleType : _app.SystemTypes.SystemDoubleType;
        var (context, definition, parameters) = CreateMethod("NotLess", _app.SystemTypes.SystemBooleanType, [type, type]);
        var less = new LocalVariable("less", new Register(703, "less"), _app.SystemTypes.SystemBooleanType);
        var result = new LocalVariable("result", new Register(704, "result"), _app.SystemTypes.SystemBooleanType);
        var comparison = new Instruction(0, OpCode.FloatCompare, less, parameters[0], parameters[1], Imm(width), Imm(1));
        var negation = new Instruction(1, OpCode.Not, result, less);
        var graph = new ISILControlFlowGraph([comparison, negation, new(2, OpCode.Return, result)]);
        FlagConditionRecovery.Run(graph);
        SsaSimplifier.Run(graph, context.ParameterLocals);
        ConstantFolder.Run(graph);
        DeadCodeEliminator.Run(graph);
        Assert.That(negation.OpCode, Is.EqualTo(OpCode.Not));
        Emit(context, definition, graph.Instructions);
        using var runtime = Load();
        var values = FloatingValues(width);
        Assert.That(runtime.Type.GetMethod("NotLess")!.Invoke(null, [values[^1], values[0]]), Is.True,
            "Negated ordered less-than includes unordered; it is not ordered greater-or-equal.");
    }

    [TestCase("integer-left")]
    [TestCase("integer-right")]
    [TestCase("wrong-float-width")]
    [TestCase("mixed-float-width")]
    [TestCase("retagged-parameter")]
    [TestCase("integer-width-annotation")]
    [TestCase("missing-metadata")]
    [TestCase("invalid-width")]
    [TestCase("negative-mask")]
    [TestCase("out-of-range-mask")]
    [TestCase("integer-literal")]
    [TestCase("float-literal")]
    [TestCase("double-literal")]
    [TestCase("memory")]
    [TestCase("nonboolean-destination")]
    public void FloatingComparisonRejectsUnprovedRepresentation(string invalidity)
    {
        var types = _app.SystemTypes;
        var leftType = invalidity is "integer-left" or "retagged-parameter" ? types.SystemInt32Type : types.SystemSingleType;
        var rightType = invalidity == "integer-right" ? types.SystemInt32Type :
            invalidity == "mixed-float-width" ? types.SystemDoubleType : types.SystemSingleType;
        var resultType = invalidity == "nonboolean-destination" ? types.SystemInt32Type : types.SystemBooleanType;
        var (context, definition, parameters) = CreateMethod("InvalidFloat", resultType, [leftType, rightType]);
        var result = new LocalVariable("result", new Register(705, "result"), resultType);
        var instruction = new Instruction(0, OpCode.FloatCompare, result, parameters[0], parameters[1], Imm(32), Imm(2));
        switch (invalidity)
        {
            case "retagged-parameter": parameters[0].Type = types.SystemSingleType; break;
            case "wrong-float-width": instruction.SetOperand(3, Imm(64)); break;
            case "integer-width-annotation": instruction.IntegerBitWidth = 32; break;
            case "missing-metadata": instruction.RemoveOperandAt(4); break;
            case "invalid-width": instruction.SetOperand(3, Imm(16)); break;
            case "negative-mask": instruction.SetOperand(4, Imm(-1)); break;
            case "out-of-range-mask": instruction.SetOperand(4, Imm(16)); break;
            case "integer-literal": instruction.SetOperand(1, Imm(0)); break;
            case "float-literal": instruction.SetOperand(1, new FloatLiteral(0)); break;
            case "double-literal": instruction.SetOperand(1, new DoubleLiteral(0)); break;
            case "memory": instruction.SetOperand(1, new MemoryOperand(parameters[0])); break;
        }
        Assert.That(() => Emit(context, definition, [instruction, new(1, OpCode.Return, result)]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating comparison"));
    }

    [Test]
    public void FloatingPredicateMetadataSurvivesCopyPropagationAndControlsEquality()
    {
        var type = _app.SystemTypes.SystemDoubleType;
        var (context, _, parameters) = CreateMethod("Predicate", _app.SystemTypes.SystemBooleanType, [type, type]);
        var copied = new LocalVariable("copy", new Register(706, "copy"), type);
        var result = new LocalVariable("result", new Register(707, "result"), _app.SystemTypes.SystemBooleanType);
        var comparison = new Instruction(1, OpCode.FloatCompare, result, copied, parameters[1], Imm(64), Imm(10));
        var graph = new ISILControlFlowGraph([new(0, OpCode.Move, copied, parameters[0]), comparison, new(2, OpCode.Return, result)]);
        SsaSimplifier.Run(graph, context.ParameterLocals);
        var expected = new Instruction(1, OpCode.FloatCompare, result, parameters[0], parameters[1], Imm(64), Imm(10));
        Assert.That(comparison.IsStructurallyEqualTo(expected), Is.True);
        expected.SetOperand(3, Imm(32));
        Assert.That(comparison.IsStructurallyEqualTo(expected), Is.False);
        expected.SetOperand(3, Imm(64));
        expected.SetOperand(4, Imm(2));
        Assert.That(comparison.IsStructurallyEqualTo(expected), Is.False);
        Assert.That(comparison.Sources, Is.EquivalentTo(parameters));
    }

    [TestCase("valid", true)]
    [TestCase("memory", false)]
    [TestCase("missing-metadata", false)]
    [TestCase("invalid-width", false)]
    [TestCase("invalid-mask", false)]
    [TestCase("integer-width-annotation", false)]
    public void DeadFloatingFlagsRequirePureOperandsAndValidMetadata(string shape, bool removed)
    {
        var type = _app.SystemTypes.SystemSingleType;
        var (_, _, parameters) = CreateMethod("Unused", _app.SystemTypes.SystemBooleanType, [type, type]);
        var result = new LocalVariable("result", new Register(708, "result"), _app.SystemTypes.SystemBooleanType);
        var comparison = new Instruction(0, OpCode.FloatCompare, result, parameters[0], parameters[1], Imm(32), Imm(8));
        switch (shape)
        {
            case "memory": comparison.SetOperand(1, new MemoryOperand(parameters[0])); break;
            case "missing-metadata": comparison.RemoveOperandAt(4); break;
            case "invalid-width": comparison.SetOperand(3, Imm(8)); break;
            case "invalid-mask": comparison.SetOperand(4, Imm(16)); break;
            case "integer-width-annotation": comparison.IntegerBitWidth = 32; break;
        }
        DeadCodeEliminator.Run(new ISILControlFlowGraph([comparison, new(1, OpCode.Return, Imm(0))]));
        Assert.That(comparison.OpCode == OpCode.Nop, Is.EqualTo(removed));
    }

    [Test]
    public void FloatingGuardCannotBeDiscardedAsAnInjectedArrayBoundsCheck()
    {
        var type = _app.SystemTypes.SystemSingleType;
        var (_, _, parameters) = CreateMethod("Guard", _app.SystemTypes.SystemVoidType, [type, type]);
        var result = new LocalVariable("result", new Register(709, "result"), _app.SystemTypes.SystemBooleanType);
        var exception = type.DeclaringAssembly.GetTypeByFullName("System.IndexOutOfRangeException")!;
        Assert.That(exception, Is.Not.Null);
        var throwing = new Instruction(3, OpCode.Throw, exception);
        var branch = new Instruction(1, OpCode.ConditionalJump, throwing, result);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.FloatCompare, result, parameters[0], parameters[1], Imm(32), Imm(8)),
            branch, new(2, OpCode.Return), throwing,
        ]);
        InjectedCheckRemover.Run(graph);
        Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        Assert.That(graph.Instructions, Does.Contain(throwing));
    }

    private static object[] FloatingValues(int width)
    {
        if (width == 32)
        {
            uint[] bits = [0, 0x80000000, 0x3F800000, 0xBF800000, 1, 0x80000001, 0x00800000,
                0x80800000, 0x7F7FFFFF, 0xFF7FFFFF, 0x7F800000, 0xFF800000, 0x7FC00001, 0xFFC12345];
            return bits.Select(value => (object)BitConverter.Int32BitsToSingle(unchecked((int)value))).ToArray();
        }
        ulong[] wideBits = [0, 0x8000000000000000, 0x3FF0000000000000, 0xBFF0000000000000,
            1, 0x8000000000000001, 0x0010000000000000, 0x8010000000000000, 0x7FEFFFFFFFFFFFFF,
            0xFFEFFFFFFFFFFFFF, 0x7FF0000000000000, 0xFFF0000000000000, 0x7FF8000000000001, 0xFFF8123456789ABC];
        return wideBits.Select(value => (object)BitConverter.Int64BitsToDouble(unchecked((long)value))).ToArray();
    }
}
