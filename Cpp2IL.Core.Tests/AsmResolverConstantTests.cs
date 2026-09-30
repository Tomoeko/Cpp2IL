using System;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.Tests;

public class AsmResolverConstantTests
{
    [TestCase(32, "00000000")]
    [TestCase(32, "80000000")]
    [TestCase(32, "00000001")]
    [TestCase(32, "7FC00001")]
    [TestCase(32, "FFC00002")]
    [TestCase(64, "0000000000000000")]
    [TestCase(64, "8000000000000000")]
    [TestCase(64, "0000000000000001")]
    [TestCase(64, "7FF8000000000001")]
    [TestCase(64, "FFF8000000000002")]
    public void FloatingMetadataConstantsRetainTheirTypeAndExactBits(int width, string encoded)
    {
        var bits = Convert.ToUInt64(encoded, 16);
        object value = width == 32
            ? (object)BitConverter.Int32BitsToSingle(unchecked((int)bits))
            : BitConverter.Int64BitsToDouble(unchecked((long)bits));
        var constant = AsmResolverConstants.GetOrCreateConstant(value);
        Assert.That(constant.Type, Is.EqualTo(width == 32 ? ElementType.R4 : ElementType.R8));
        var bytes = constant.Value!.Data;
        Assert.That(bytes.Length, Is.EqualTo(width / 8));
        var actual = width == 32 ? BitConverter.ToUInt32(bytes, 0) : BitConverter.ToUInt64(bytes, 0);
        Assert.That(actual, Is.EqualTo(bits));
    }

    [Test]
    public void OnlyPositiveSingleZeroUsesTheSharedCacheEntry()
    {
        var positive = AsmResolverConstants.GetOrCreateConstant(0.0F);
        var negative = AsmResolverConstants.GetOrCreateConstant(BitConverter.Int32BitsToSingle(int.MinValue));
        Assert.That(AsmResolverConstants.GetOrCreateConstant(0.0F), Is.SameAs(positive));
        Assert.That(negative, Is.Not.SameAs(positive));
        Assert.That(BitConverter.ToUInt32(positive.Value!.Data, 0), Is.Zero);
        Assert.That(BitConverter.ToUInt32(negative.Value!.Data, 0), Is.EqualTo(0x80000000U));
    }
}
