using System;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests.Isil;

public class NativeInvocationFloatingLiteralTests
{
    [TestCase(32, "00000000")]
    [TestCase(32, "80000000")]
    [TestCase(32, "3F000000")]
    [TestCase(32, "7FC00001")]
    [TestCase(32, "7FC00002")]
    [TestCase(64, "0000000000000000")]
    [TestCase(64, "8000000000000000")]
    [TestCase(64, "3FE0000000000000")]
    [TestCase(64, "7FF8000000000001")]
    [TestCase(64, "7FF8000000000002")]
    public void ReinterpretedStoreLiteralsPreserveSignedZeroAndNanPayloads(int width, string encoded)
    {
        var bits = Convert.ToUInt64(encoded, 16);
        IOperand typed = width == 32
            ? new FloatLiteral(BitConverter.Int32BitsToSingle(unchecked((int)bits)))
            : new DoubleLiteral(BitConverter.Int64BitsToDouble(unchecked((long)bits)));
        Assert.That(X64NativeNullCheckedInvocationProof.TryFloatingLiteralBits(
            new Immediate(unchecked((long)bits)), width, out var raw), Is.True);
        Assert.That(X64NativeNullCheckedInvocationProof.TryFloatingLiteralBits(typed, width, out var recovered), Is.True);
        Assert.That(raw, Is.EqualTo(bits));
        Assert.That(recovered, Is.EqualTo(bits));
    }

    [Test]
    public void FloatingEqualityCannotSubstituteForBitIdentityOrWidth()
    {
        var first = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC00001));
        var second = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC00002));
        Assert.That(first.Equals(second), Is.True, "Managed NaN equality loses payload identity.");
        Assert.That(X64NativeNullCheckedInvocationProof.TryFloatingLiteralBits(new FloatLiteral(first), 32, out var left), Is.True);
        Assert.That(X64NativeNullCheckedInvocationProof.TryFloatingLiteralBits(new FloatLiteral(second), 32, out var right), Is.True);
        Assert.That(left, Is.Not.EqualTo(right));
        Assert.That(X64NativeNullCheckedInvocationProof.TryFloatingLiteralBits(new FloatLiteral(0), 64, out _), Is.False);
        Assert.That(X64NativeNullCheckedInvocationProof.TryFloatingLiteralBits(new DoubleLiteral(0), 32, out _), Is.False);
        Assert.That(X64NativeNullCheckedInvocationProof.TryFloatingLiteralBits(new Immediate(0x1_0000_0000), 32, out _), Is.False);
        Assert.That(X64NativeNullCheckedInvocationProof.TryFloatingLiteralBits(new Immediate(0), 16, out _), Is.False);
    }
}
