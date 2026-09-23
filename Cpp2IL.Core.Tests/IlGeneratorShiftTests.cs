using System;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(32, false)]
    [TestCase(32, true)]
    [TestCase(64, false)]
    [TestCase(64, true)]
    public void RightShiftsPreserveSignednessAndCountMask(int width, bool logical)
    {
        var valueType = (width, logical) switch
        {
            (32, false) => _app.SystemTypes.SystemInt32Type,
            (32, true) => _app.SystemTypes.SystemUInt32Type,
            (64, false) => _app.SystemTypes.SystemInt64Type,
            _ => _app.SystemTypes.SystemUInt64Type,
        };
        var (context, definition, parameters) = CreateMethod("Shift", valueType, [valueType, _app.SystemTypes.SystemInt32Type]);
        var result = new LocalVariable("result", new Register(500, "result"), valueType);
        context.Locals.Add(result);
        Emit(context, definition,
        [
            new(0, logical ? OpCode.ShiftRightUnsigned : OpCode.ShiftRight, result, parameters[0], parameters[1]) { IntegerBitWidth = width },
            new(1, OpCode.Return, result),
        ]);
        using var runtime = Load();
        var method = runtime.Type.GetMethod("Shift")!;
        ulong[] values = width == 32 ? [0, 1, int.MaxValue, 1UL << 31, uint.MaxValue] : [0, 1, long.MaxValue, 1UL << 63, ulong.MaxValue];
        int[] counts = [-65, -64, -33, -32, -1, 0, 1, 31, 32, 33, 63, 64, 65];
        foreach (var bits in values)
        foreach (var count in counts)
        {
            object value = (width, logical) switch
            {
                (32, false) => (object)unchecked((int)bits),
                (32, true) => (uint)bits,
                (64, false) => unchecked((long)bits),
                _ => bits,
            };
            object expected = (width, logical) switch
            {
                (32, false) => (object)(unchecked((int)bits) >> count),
                (32, true) => (uint)bits >> count,
                (64, false) => unchecked((long)bits) >> count,
                _ => bits >> count,
            };
            Assert.That(method.Invoke(null, [value, count]), Is.EqualTo(expected), $"{bits}, {count}");
        }
    }

    [TestCase(32)]
    [TestCase(64)]
    public void Integer64ShiftCountIsNarrowedBeforeMasking(int width)
    {
        var valueType = width == 32 ? _app.SystemTypes.SystemUInt32Type : _app.SystemTypes.SystemUInt64Type;
        var (context, definition, parameters) = CreateMethod("Shift", valueType, [valueType, _app.SystemTypes.SystemInt64Type]);
        Emit(context, definition,
        [
            new(0, OpCode.ShiftRightUnsigned, parameters[0], parameters[0], parameters[1]) { IntegerBitWidth = width },
            new(1, OpCode.Return, parameters[0]),
        ]);
        using var runtime = Load();
        var method = runtime.Type.GetMethod("Shift")!;
        object value = width == 32 ? (object)uint.MaxValue : ulong.MaxValue;
        object expected = width == 32 ? (object)(uint.MaxValue >> 1) : ulong.MaxValue >> 1;
        Assert.That(method.Invoke(null, [value, 0x100000001L]), Is.EqualTo(expected));
        Assert.That(method.Invoke(null, [value, -63L]), Is.EqualTo(expected));
    }

    [TestCase(0)]
    [TestCase(8)]
    [TestCase(16)]
    public void ShiftWidthCannotBeGuessedFromReturnType(int nativeWidth)
    {
        var (context, definition, parameters) = CreateMethod("Shift", _app.SystemTypes.SystemInt32Type, [_app.SystemTypes.SystemInt32Type]);
        Assert.That(() => Emit(context, definition,
        [
            new(0, OpCode.ShiftRight, parameters[0], parameters[0], Imm(1)) { IntegerBitWidth = nativeWidth },
            new(1, OpCode.Return, parameters[0]),
        ]), Throws.TypeOf<DecompilerException>().With.Message.Contains("established 32/64-bit"));
    }

    [Test]
    public void Native32ShiftCannotInvent64BitResultExtension()
    {
        var (context, definition, parameters) = CreateMethod("Shift", _app.SystemTypes.SystemInt64Type, [_app.SystemTypes.SystemInt32Type]);
        var result = new LocalVariable("result", new Register(501, "result"), _app.SystemTypes.SystemInt64Type);
        context.Locals.Add(result);
        Assert.That(() => Emit(context, definition,
        [
            new(0, OpCode.ShiftRightUnsigned, result, parameters[0], Imm(1)) { IntegerBitWidth = 32 },
            new(1, OpCode.Return, result),
        ]), Throws.TypeOf<DecompilerException>().With.Message.Contains("destination"));
    }

    [Test]
    public void FloatingPointCountCannotBecomeAnIntegerShift()
    {
        var (context, definition, parameters) = CreateMethod("Shift", _app.SystemTypes.SystemInt32Type,
            [_app.SystemTypes.SystemInt32Type, _app.SystemTypes.SystemSingleType]);
        Assert.That(() => Emit(context, definition,
        [
            new(0, OpCode.ShiftRight, parameters[0], parameters[0], parameters[1]) { IntegerBitWidth = 32 },
            new(1, OpCode.Return, parameters[0]),
        ]), Throws.TypeOf<DecompilerException>().With.Message.Contains("Shift count"));
    }
}
