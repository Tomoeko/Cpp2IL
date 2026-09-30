using System;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [Test]
    public void NarrowingAndDivisionRoundBeforeTheOrderedNegativeSelection()
    {
        var types = _app.SystemTypes;
        var (context, definition, parameters) = CreateMethod("NarrowQuotient", types.SystemSingleType,
            [types.SystemDoubleType, types.SystemSingleType]);
        var narrowed = new LocalVariable("narrowed", new Register(890, "narrowed"), types.SystemSingleType);
        var quotient = new LocalVariable("quotient", new Register(891, "quotient"), types.SystemSingleType);
        var result = new LocalVariable("result", new Register(892, "result"), types.SystemSingleType);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.FloatConvert, narrowed, parameters[0], Imm(64), Imm(32)),
            new(1, OpCode.FloatDivide, quotient, narrowed, parameters[1], Imm(32)),
            new(2, OpCode.FloatNegateNegative, result, quotient, Imm(32)),
            new(3, OpCode.Return, result)]);
        ConstantFolder.Run(graph);
        DeadCodeEliminator.Run(graph);
        Emit(context, definition, graph.Instructions);
        using var runtime = Load();
        var method = runtime.Type.GetMethod("NarrowQuotient")!;
        var cases = new (ulong Source, uint Divisor, uint Expected)[]
        {
            (0x47effffff0000000, 0x7f7fffff, 0x7f800000), // Narrowing overflows before division.
            (0x3690000000000000, 1, 0), // Narrowing ties to zero before division.
            (0x3690000000000001, 1, 0x3f800000),
            (0x36a0000000000000, 0x40000000, 0), // Division also ties to even.
            (0x36b8000000000000, 0x40000000, 2),
            (0x8000000000000000, 0x3f800000, 0x80000000), // Ordered comparison retains negative zero.
            (0xbff0000000000000, 0x40400000, 0x3eaaaaab),
            (0xfff0000000000000, 0x3f800000, 0x7f800000),
        };
        foreach (var (source, divisor, expected) in cases)
        {
            var actual = (float)method.Invoke(null, [BitConverter.Int64BitsToDouble(unchecked((long)source)),
                BitConverter.Int32BitsToSingle(unchecked((int)divisor))])!;
            Assert.That(unchecked((uint)BitConverter.SingleToInt32Bits(actual)), Is.EqualTo(expected));
        }
    }

    [TestCase(32)]
    [TestCase(64)]
    public void OrderedNegativeSelectionKeepsZeroSignsAndUnorderedPayloads(int width)
    {
        var type = width == 32 ? _app.SystemTypes.SystemSingleType : _app.SystemTypes.SystemDoubleType;
        var (context, definition, parameters) = CreateMethod("OrderedSign", type, [type]);
        var result = new LocalVariable("result", new Register(893, "result"), type);
        Emit(context, definition, [new(0, OpCode.FloatNegateNegative, result, parameters[0], Imm(width)),
            new(1, OpCode.Return, result)]);
        using var runtime = Load();
        var method = runtime.Type.GetMethod("OrderedSign")!;
        var cases = width == 32 ? new (ulong Input, ulong Expected)[]
            { (0, 0), (0x80000000, 0x80000000), (0xbf800000, 0x3f800000), (0xffc00002, 0xffc00002) }
            : [(0, 0), (0x8000000000000000, 0x8000000000000000),
                (0xbff0000000000000, 0x3ff0000000000000), (0xfff8000000000002, 0xfff8000000000002)];
        foreach (var (input, expected) in cases)
        {
            object value = width == 32 ? BitConverter.Int32BitsToSingle(unchecked((int)input)) :
                (object)BitConverter.Int64BitsToDouble(unchecked((long)input));
            var actual = method.Invoke(null, [value])!;
            var bits = width == 32 ? unchecked((uint)BitConverter.SingleToInt32Bits((float)actual)) :
                unchecked((ulong)BitConverter.DoubleToInt64Bits((double)actual));
            Assert.That(bits, Is.EqualTo(expected));
        }
    }

    [TestCase("divisor-type")]
    [TestCase("retagged-divisor")]
    [TestCase("unsupported-width")]
    [TestCase("integer-width")]
    public void FloatingDivisionRejectsUnprovedTypesAndPrecision(string invalidity)
    {
        var types = _app.SystemTypes;
        var divisorType = invalidity is "divisor-type" or "retagged-divisor" ? types.SystemInt32Type : types.SystemSingleType;
        var (context, definition, parameters) = CreateMethod("InvalidDivision", types.SystemSingleType,
            [types.SystemSingleType, divisorType]);
        if (invalidity == "retagged-divisor") parameters[1].Type = types.SystemSingleType;
        var result = new LocalVariable("result", new Register(894, "result"), types.SystemSingleType);
        var division = new Instruction(0, OpCode.FloatDivide, result, parameters[0], parameters[1], Imm(32));
        if (invalidity == "unsupported-width") division.SetOperand(3, Imm(16));
        if (invalidity == "integer-width") division.IntegerBitWidth = 32;
        Assert.That(() => Emit(context, definition, [division, new(1, OpCode.Return, result)]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating"));
    }
}
