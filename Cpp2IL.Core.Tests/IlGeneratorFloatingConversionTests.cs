using System;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(32)]
    [TestCase(64)]
    public void FloatingConversionPreservesSpecifiedIeeeBoundaryResultBits(int sourceWidth)
    {
        var types = _app.SystemTypes;
        var sourceType = sourceWidth == 32 ? types.SystemSingleType : types.SystemDoubleType;
        var resultType = sourceWidth == 32 ? types.SystemDoubleType : types.SystemSingleType;
        var (context, definition, parameters) = CreateMethod("Convert", resultType, [sourceType]);
        var result = new LocalVariable("result", new Register(870, "floatConversionResult"), resultType);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.FloatConvert, result, parameters[0], Imm(sourceWidth), Imm(sourceWidth == 32 ? 64 : 32)),
            new(1, OpCode.Return, result)]);
        ConstantFolder.Run(graph);
        DeadCodeEliminator.Run(graph);
        Emit(context, definition, graph.Instructions);
        using var runtime = Load();
        var method = runtime.Type.GetMethod("Convert")!;
        var cases = sourceWidth == 32
            ? new (ulong Input, ulong Expected)[]
            {
                (0, 0), (0x80000000, 0x8000000000000000), (1, 0x36a0000000000000),
                (0x3f800000, 0x3ff0000000000000), (0x7f800000, 0x7ff0000000000000),
                (0x7fc00001, 0x7ff8000020000000), (0xff800002, 0xfff8000040000000)
            }
            : new (ulong Input, ulong Expected)[]
            {
                (0, 0), (0x8000000000000000, 0x80000000), (0x3ff0000010000000, 0x3f800000),
                (0x3ff0000010000001, 0x3f800001), (0x3ff0000030000000, 0x3f800002),
                (0x47effffff0000000, 0x7f800000), (0x3690000000000000, 0),
                (0x3690000000000001, 1), (0xfff0000000000002, 0xffc00000)
            };
        foreach (var (input, expected) in cases)
        {
            object value = sourceWidth == 32 ? BitConverter.Int32BitsToSingle(unchecked((int)input)) :
                (object)BitConverter.Int64BitsToDouble(unchecked((long)input));
            var actual = method.Invoke(null, [value])!;
            var bits = sourceWidth == 32 ? unchecked((ulong)BitConverter.DoubleToInt64Bits((double)actual)) :
                unchecked((uint)BitConverter.SingleToInt32Bits((float)actual));
            Assert.That(bits, Is.EqualTo(expected), $"sourceWidth={sourceWidth}, input={input:X16}");
        }
    }

    [TestCase("source-type")]
    [TestCase("result-type")]
    [TestCase("retagged-parameter")]
    [TestCase("equal-widths")]
    [TestCase("missing-width")]
    [TestCase("integer-width")]
    [TestCase("unsupported-width")]
    public void FloatingConversionRejectsUnprovedStorageOrOperationMetadata(string invalidity)
    {
        var types = _app.SystemTypes;
        var sourceType = invalidity is "source-type" or "retagged-parameter" ? types.SystemInt32Type : types.SystemSingleType;
        var resultType = invalidity == "result-type" ? types.SystemInt32Type : types.SystemDoubleType;
        var (context, definition, parameters) = CreateMethod("InvalidConversion", resultType, [sourceType]);
        if (invalidity == "retagged-parameter") parameters[0].Type = types.SystemSingleType;
        var result = new LocalVariable("result", new Register(871, "floatConversionResult"), resultType);
        var instruction = new Instruction(0, OpCode.FloatConvert, result, parameters[0], Imm(32), Imm(64));
        switch (invalidity)
        {
            case "equal-widths": instruction.SetOperand(3, Imm(32)); break;
            case "missing-width": instruction.RemoveOperandAt(3); break;
            case "integer-width": instruction.IntegerBitWidth = 32; break;
            case "unsupported-width": instruction.SetOperand(2, Imm(16)); break;
        }
        Assert.That(() => Emit(context, definition, [instruction, new(1, OpCode.Return, result)]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating conversion"));
    }
}
