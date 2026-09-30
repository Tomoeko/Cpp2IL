using System;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(32)]
    [TestCase(64)]
    public void FloatingProjectionRetainsTypedCopyAndPositiveZeroResultBits(int width)
    {
        var type = width == 32 ? _app.SystemTypes.SystemSingleType : _app.SystemTypes.SystemDoubleType;
        foreach (var zero in new[] { false, true })
        {
            var (context, definition, parameters) = CreateMethod(zero ? "Zero" : "Copy", type, [type]);
            var result = new LocalVariable("result", new Register(862, "projectedResult"), type);
            IOperand source = zero ? width == 32 ? new FloatLiteral(0f) : new DoubleLiteral(0d) : parameters[0];
            Emit(context, definition, [new(0, OpCode.FloatProject, result, source, Imm(width)),
                new(1, OpCode.Return, result)]);
        }
        using var runtime = Load();
        foreach (var bits in SelectionBits(width))
        {
            object value = width == 32 ? (object)BitConverter.Int32BitsToSingle(unchecked((int)bits)) :
                BitConverter.Int64BitsToDouble(unchecked((long)bits));
            foreach (var name in new[] { "Copy", "Zero" })
            {
                var actual = runtime.Type.GetMethod(name)!.Invoke(null, [value])!;
                var actualBits = width == 32 ? unchecked((uint)BitConverter.SingleToInt32Bits((float)actual)) :
                    unchecked((ulong)BitConverter.DoubleToInt64Bits((double)actual));
                Assert.That(actualBits, Is.EqualTo(name == "Copy" ? bits : 0UL));
            }
        }
    }

    [TestCase("double-source")]
    [TestCase("double-destination")]
    [TestCase("retagged-parameter")]
    [TestCase("negative-zero")]
    [TestCase("nonzero")]
    [TestCase("wrong-literal-width")]
    [TestCase("missing-width")]
    public void FloatingProjectionRejectsNumericConversionAndUnprovedZero(string invalidity)
    {
        var types = _app.SystemTypes;
        var sourceType = invalidity is "double-source" or "retagged-parameter" ? types.SystemDoubleType : types.SystemSingleType;
        var resultType = invalidity == "double-destination" ? types.SystemDoubleType : types.SystemSingleType;
        var (context, definition, parameters) = CreateMethod("InvalidProject", resultType, [sourceType]);
        if (invalidity == "retagged-parameter") parameters[0].Type = types.SystemSingleType;
        var result = new LocalVariable("result", new Register(863, "projectedResult"), resultType);
        var operation = new Instruction(0, OpCode.FloatProject, result, parameters[0], Imm(32));
        switch (invalidity)
        {
            case "negative-zero": operation.SetOperand(1, new FloatLiteral(-0f)); break;
            case "nonzero": operation.SetOperand(1, new FloatLiteral(1f)); break;
            case "wrong-literal-width": operation.SetOperand(1, new DoubleLiteral(0d)); break;
            case "missing-width": operation.RemoveOperandAt(2); break;
        }
        Assert.That(() => Emit(context, definition, [operation, new(1, OpCode.Return, result)]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating projection"));
    }

    [TestCase(32, false)]
    [TestCase(32, true)]
    [TestCase(64, false)]
    [TestCase(64, true)]
    public void FloatingSelectionPreservesSecondOperandBitsOnEqualAndUnorderedInputs(int width, bool maximum)
    {
        var type = width == 32 ? _app.SystemTypes.SystemSingleType : _app.SystemTypes.SystemDoubleType;
        var (context, definition, parameters) = CreateMethod("Select", type, [type, type]);
        var result = new LocalVariable("result", new Register(860, "floatResult"), type);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.FloatSelect, result, parameters[0], parameters[1], Imm(width), Imm(maximum ? 1 : 0)),
            new(1, OpCode.Return, result)]);
        ConstantFolder.Run(graph);
        DeadCodeEliminator.Run(graph);
        Emit(context, definition, graph.Instructions);
        using var runtime = Load();
        var method = runtime.Type.GetMethod("Select")!;
        foreach (var leftBits in SelectionBits(width))
        foreach (var rightBits in SelectionBits(width))
        {
            object left = width == 32 ? (object)BitConverter.Int32BitsToSingle(unchecked((int)leftBits)) :
                BitConverter.Int64BitsToDouble(unchecked((long)leftBits));
            object right = width == 32 ? (object)BitConverter.Int32BitsToSingle(unchecked((int)rightBits)) :
                BitConverter.Int64BitsToDouble(unchecked((long)rightBits));
            var chooseLeft = maximum ? Convert.ToDouble(left) > Convert.ToDouble(right) :
                Convert.ToDouble(left) < Convert.ToDouble(right);
            var actual = method.Invoke(null, [left, right])!;
            var actualBits = width == 32 ? unchecked((uint)BitConverter.SingleToInt32Bits((float)actual)) :
                unchecked((ulong)BitConverter.DoubleToInt64Bits((double)actual));
            Assert.That(actualBits, Is.EqualTo(chooseLeft ? leftBits : rightBits),
                $"width={width}, maximum={maximum}, left={leftBits:X16}, right={rightBits:X16}");
        }
    }

    private static ulong[] SelectionBits(int width) => width == 32
        ? [0, 0x80000000, 0x3f800000, 0xbf800000, 1, 0x80000001, 0x00800000,
            0x7f7fffff, 0xff7fffff, 0x7f800000, 0xff800000, 0x7fc00001, 0xffc00002,
            0x7f800001, 0xff800002]
        : [0, 0x8000000000000000, 0x3ff0000000000000, 0xbff0000000000000, 1,
            0x8000000000000001, 0x0010000000000000, 0x7fefffffffffffff, 0xffefffffffffffff,
            0x7ff0000000000000, 0xfff0000000000000, 0x7ff8000000000001, 0xfff8000000000002,
            0x7ff0000000000001, 0xfff0000000000002];

    [TestCase("integer-left")]
    [TestCase("double-right")]
    [TestCase("retagged-parameter")]
    [TestCase("integer-destination")]
    [TestCase("width")]
    [TestCase("mode")]
    [TestCase("missing-metadata")]
    [TestCase("extra-metadata")]
    [TestCase("integer-annotation")]
    [TestCase("memory")]
    public void FloatingSelectionRejectsUnprovedRepresentation(string invalidity)
    {
        var types = _app.SystemTypes;
        var leftType = invalidity is "integer-left" or "retagged-parameter" ? types.SystemInt32Type : types.SystemSingleType;
        var rightType = invalidity == "double-right" ? types.SystemDoubleType : types.SystemSingleType;
        var resultType = invalidity == "integer-destination" ? types.SystemInt32Type : types.SystemSingleType;
        var (context, definition, parameters) = CreateMethod("InvalidSelect", resultType, [leftType, rightType]);
        if (invalidity == "retagged-parameter") parameters[0].Type = types.SystemSingleType;
        var result = new LocalVariable("result", new Register(861, "floatResult"), resultType);
        var selection = new Instruction(0, OpCode.FloatSelect, result, parameters[0], parameters[1], Imm(32), Imm(1));
        switch (invalidity)
        {
            case "width": selection.SetOperand(3, Imm(16)); break;
            case "mode": selection.SetOperand(4, Imm(2)); break;
            case "missing-metadata": selection.RemoveOperandAt(4); break;
            case "extra-metadata": selection.AddOperands([Imm(0)]); break;
            case "integer-annotation": selection.IntegerBitWidth = 32; break;
            case "memory": selection.SetOperand(1, new MemoryOperand(parameters[0])); break;
        }
        Assert.That(() => Emit(context, definition, [selection, new(1, OpCode.Return, result)]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating selection"));
    }
}
