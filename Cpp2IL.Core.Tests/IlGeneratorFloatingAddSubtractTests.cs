using System;
using System.Linq;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(32)]
    [TestCase(64)]
    public void FloatingSubtractionAndAdditionRoundSeparatelyAndKeepZeroSigns(int width)
    {
        var type = width == 32 ? _app.SystemTypes.SystemSingleType : _app.SystemTypes.SystemDoubleType;
        var (context, definition, parameters) = CreateMethod("RoundedDelta", type, [type, type, type]);
        var delta = new LocalVariable("delta", new Register(895, "delta"), type);
        var sum = new LocalVariable("sum", new Register(896, "sum"), type);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.FloatSubtract, delta, parameters[0], parameters[1], Imm(width)),
            new(1, OpCode.FloatAdd, sum, delta, parameters[2], Imm(width)),
            new(2, OpCode.Return, sum)]);
        ConstantFolder.Run(graph);
        DeadCodeEliminator.Run(graph);
        Emit(context, definition, graph.Instructions);
        var code = definition.CilMethodBody!.Instructions;
        foreach (var operation in new[] { CilOpCodes.Sub, CilOpCodes.Add })
        {
            var position = code.ToList().FindIndex(instruction => instruction.OpCode == operation);
            Assert.That(position, Is.GreaterThanOrEqualTo(0));
            Assert.That(code[position + 1].OpCode, Is.EqualTo(width == 32 ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8));
        }
        using var runtime = Load();
        var method = runtime.Type.GetMethod("RoundedDelta")!;
        var cases = width == 32 ? new (ulong Current, ulong Baseline, ulong Total, ulong Expected)[]
        {
            (0x4b800000, 0xbf800000, 0xcb800000, 0), // Regrouping gives one instead.
            (0x80000000, 0, 0x80000000, 0x80000000),
            (0, 0x80000000, 0x80000000, 0),
            (1, 0x80000001, 0x80000001, 1),
            (0x7f7fffff, 0xff7fffff, 0xff7fffff, 0x7f800000),
            (0xffc00042, 0x3f800000, 0, 0xffc00042),
        } :
        [
            (0x4340000000000000, 0xbff0000000000000, 0xc340000000000000, 0),
            (0x8000000000000000, 0, 0x8000000000000000, 0x8000000000000000),
            (0, 0x8000000000000000, 0x8000000000000000, 0),
            (1, 0x8000000000000001, 0x8000000000000001, 1),
            (0x7fefffffffffffff, 0xffefffffffffffff, 0xffefffffffffffff, 0x7ff0000000000000),
            (0xfff8000000000042, 0x3ff0000000000000, 0, 0xfff8000000000042),
        ];
        foreach (var (current, baseline, total, expected) in cases)
        {
            var actual = method.Invoke(null, [Value(current), Value(baseline), Value(total)])!;
            var bits = width == 32 ? unchecked((uint)BitConverter.SingleToInt32Bits((float)actual)) :
                unchecked((ulong)BitConverter.DoubleToInt64Bits((double)actual));
            Assert.That(bits, Is.EqualTo(expected));
        }

        object Value(ulong bits) => width == 32 ? (object)BitConverter.Int32BitsToSingle(unchecked((int)bits)) :
            BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }

    [TestCase(OpCode.FloatAdd, "parameter-type")]
    [TestCase(OpCode.FloatSubtract, "parameter-type")]
    [TestCase(OpCode.FloatAdd, "retagged-parameter")]
    [TestCase(OpCode.FloatSubtract, "retagged-parameter")]
    [TestCase(OpCode.FloatAdd, "precision")]
    [TestCase(OpCode.FloatSubtract, "precision")]
    [TestCase(OpCode.FloatAdd, "integer-width")]
    [TestCase(OpCode.FloatSubtract, "integer-width")]
    public void FloatingAddSubtractCannotInventAnOperandTypeOrPrecision(OpCode operation, string mutation)
    {
        var types = _app.SystemTypes;
        var parameterType = mutation is "parameter-type" or "retagged-parameter" ? types.SystemInt64Type : types.SystemDoubleType;
        var (context, definition, parameters) = CreateMethod("InvalidFloatingArithmetic", types.SystemDoubleType,
            [types.SystemDoubleType, parameterType]);
        if (mutation == "retagged-parameter") parameters[1].Type = types.SystemDoubleType;
        var result = new LocalVariable("result", new Register(897, "result"), types.SystemDoubleType);
        var arithmetic = new Instruction(0, operation, result, parameters[0], parameters[1], Imm(mutation == "precision" ? 16 : 64));
        if (mutation == "integer-width") arithmetic.IntegerBitWidth = 64;
        Assert.That(() => Emit(context, definition, [arithmetic, new(1, OpCode.Return, result)]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating"));
    }
}
