using System.IO;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Model.CustomAttributes;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Tests;

public class V29PrimitiveBlobTests
{
    private static CustomAttributePrimitiveParameter Read(Il2CppTypeEnum type, byte[] bytes, out long consumed)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        var parameter = new CustomAttributePrimitiveParameter(type, null!, CustomAttributeParameterKind.ConstructorParam, 0);
        parameter.ReadFromV29Blob(reader, null!); // Primitive values do not need application metadata.
        consumed = stream.Position;
        return parameter;
    }

    [Test]
    public void EmptyAndNullStringsAreDistinctAndConsumeOnlyTheirLength()
    {
        // Signed compressed lengths: 0 encodes empty, 1 encodes -1 (null).
        var empty = Read(Il2CppTypeEnum.IL2CPP_TYPE_STRING, [0, 0x71], out var emptyConsumed);
        var absent = Read(Il2CppTypeEnum.IL2CPP_TYPE_STRING, [1, 0x71], out var nullConsumed);
        Assert.Multiple(() =>
        {
            Assert.That(empty.PrimitiveValue, Is.EqualTo(string.Empty));
            Assert.That(absent.PrimitiveValue, Is.Null);
            Assert.That(emptyConsumed, Is.EqualTo(1));
            Assert.That(nullConsumed, Is.EqualTo(1));
        });
    }

    [Test]
    public void StringLengthCountsUtf8BytesWithoutConsumingFollowingArgument()
    {
        var parameter = Read(Il2CppTypeEnum.IL2CPP_TYPE_STRING, [4, 0xC3, 0xA9, 0x71], out var consumed);
        Assert.That(parameter.PrimitiveValue, Is.EqualTo("\u00E9"));
        Assert.That(consumed, Is.EqualTo(3));
    }

    [TestCase(0x0041)]
    [TestCase(0x03BB)]
    [TestCase(0xD800)]
    public void CharUsesExactlyTwoBytes(int codeUnit)
    {
        var parameter = Read(Il2CppTypeEnum.IL2CPP_TYPE_CHAR, [(byte)codeUnit, (byte)(codeUnit >> 8), 0x71], out var consumed);
        Assert.That(parameter.PrimitiveValue, Is.EqualTo((char)codeUnit));
        Assert.That(consumed, Is.EqualTo(2));
    }

    [Test]
    public void TruncatedStringCannotBecomeAShorterValidValue()
        => Assert.Throws<EndOfStreamException>(() => Read(Il2CppTypeEnum.IL2CPP_TYPE_STRING, [6, 0x41], out _));

    [Test]
    public void InvalidNegativeLengthCannotBecomeNull()
        => Assert.Throws<InvalidDataException>(() => Read(Il2CppTypeEnum.IL2CPP_TYPE_STRING, [3], out _));

    [TestCase(new byte[] { })]
    [TestCase(new byte[] { 0x80 })]
    [TestCase(new byte[] { 0xC0, 1, 2 })]
    [TestCase(new byte[] { 0xF0 })]
    [TestCase(new byte[] { 0xF0, 1, 2, 3 })]
    public void TruncatedCompressedIntegerCannotFabricateAValue(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        Assert.Throws<EndOfStreamException>(() => stream.ReadUnityCompressedUint());
    }
}
