using System;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(32, false)]
    [TestCase(32, true)]
    [TestCase(64, false)]
    [TestCase(64, true)]
    public void UnsignedDivisionKeepsHighBitsForQuotientAndRemainder(int width, bool remainder)
    {
        var type = width == 32 ? _app.SystemTypes.SystemUInt32Type : _app.SystemTypes.SystemUInt64Type;
        var (context, definition, parameters) = CreateMethod("Divide", type, [type, type]);
        var result = new LocalVariable("result", new Register(510, "result"), type);
        context.Locals.Add(result);
        Emit(context, definition,
        [
            new(0, remainder ? OpCode.ModuloUnsigned : OpCode.DivideUnsigned, result, parameters[0], parameters[1]) { IntegerBitWidth = width },
            new(1, OpCode.Return, result),
        ]);
        using var runtime = Load();
        var method = runtime.Type.GetMethod("Divide")!;
        var maximum = width == 32 ? uint.MaxValue : ulong.MaxValue;
        ulong[] values = [0, 1, 2, maximum / 2, maximum / 2 + 1, maximum - 1, maximum];
        foreach (var left in values)
        foreach (var right in values.Where(value => value != 0))
        {
            object expected = width == 32
                ? (object)(uint)(remainder ? left % right : left / right)
                : remainder ? left % right : left / right;
            object a = width == 32 ? (object)(uint)left : left;
            object b = width == 32 ? (object)(uint)right : right;
            Assert.That(method.Invoke(null, [a, b]), Is.EqualTo(expected), $"{left}, {right}");
        }
        object zero = width == 32 ? (object)0U : 0UL;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [zero, zero]));
        Assert.That(error!.InnerException, Is.TypeOf<DivideByZeroException>());
    }

    [TestCase(32)]
    [TestCase(64)]
    public void DiscardedNativeQuotientStillPreservesSignedOverflow(int width)
    {
        var type = width == 32 ? _app.SystemTypes.SystemInt32Type : _app.SystemTypes.SystemInt64Type;
        var (context, definition, parameters) = CreateMethod("Remainder", type, [type, type]);
        var quotient = new LocalVariable("quotient", new Register(511, "quotient"), type);
        var remainder = new LocalVariable("remainder", new Register(512, "remainder"), type);
        context.Locals.Add(quotient);
        context.Locals.Add(remainder);
        var divide = new Instruction(0, OpCode.Divide, quotient, parameters[0], parameters[1]) { IntegerBitWidth = width };
        var graph = new ISILControlFlowGraph([
            divide,
            new(1, OpCode.Modulo, remainder, parameters[0], parameters[1]) { IntegerBitWidth = width },
            new(2, OpCode.Return, remainder),
        ]);
        DeadCodeEliminator.Run(graph);
        Assert.That(graph.Instructions.Contains(divide), Is.True);
        Assert.That(divide.OpCode, Is.EqualTo(OpCode.Divide));
        Emit(context, definition, graph.Instructions.ToList());
        using var runtime = Load();
        var method = runtime.Type.GetMethod("Remainder")!;
        object minimum = width == 32 ? (object)int.MinValue : long.MinValue;
        object minusOne = width == 32 ? (object)(-1) : -1L;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [minimum, minusOne]));
        Assert.That(error!.InnerException, Is.TypeOf<OverflowException>());
        object negative = width == 32 ? (object)(-17) : -17L;
        object divisor = width == 32 ? (object)2 : 2L;
        Assert.That(method.Invoke(null, [negative, divisor]), Is.EqualTo(minusOne));
    }

    [TestCase(0)]
    [TestCase(8)]
    [TestCase(16)]
    public void UnsignedDivisionRejectsAnUnprovedWidth(int width)
    {
        var type = _app.SystemTypes.SystemUInt32Type;
        var (context, definition, parameters) = CreateMethod("Divide", type, [type, type]);
        Assert.That(() => Emit(context, definition,
        [
            new(0, OpCode.DivideUnsigned, parameters[0], parameters[0], parameters[1]) { IntegerBitWidth = width },
            new(1, OpCode.Return, parameters[0]),
        ]), Throws.TypeOf<DecompilerException>().With.Message.Contains("established 32/64-bit"));
    }

    [Test]
    public void Native32DivisionCannotInventA64BitResultExtension()
    {
        var type = _app.SystemTypes.SystemUInt32Type;
        var (context, definition, parameters) = CreateMethod("Divide", _app.SystemTypes.SystemUInt64Type, [type, type]);
        var result = new LocalVariable("result", new Register(513, "result"), _app.SystemTypes.SystemUInt64Type);
        context.Locals.Add(result);
        Assert.That(() => Emit(context, definition,
        [
            new(0, OpCode.DivideUnsigned, result, parameters[0], parameters[1]) { IntegerBitWidth = 32 },
            new(1, OpCode.Return, result),
        ]), Throws.TypeOf<DecompilerException>().With.Message.Contains("destination width"));
    }
}
