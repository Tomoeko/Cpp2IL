using System;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64FloatingFieldOperandProofTests
{
    [TestCase("0F2F4910", 32, 16)]
    [TestCase("0F2E5914", 32, 20)]
    [TestCase("660F2F4918", 64, 24)]
    [TestCase("660F2E5920", 64, 32)]
    public void ExactScalarMemoryEncodingRetainsWidthAndFieldOffset(string bytes, int width, int offset)
    {
        var instruction = Decode(bytes);
        Assert.That(X64FloatingFieldOperandProof.TryReadWidth(instruction, out var actualWidth,
            out var actualOffset), Is.True);
        Assert.That(actualWidth, Is.EqualTo(width));
        Assert.That(actualOffset, Is.EqualTo(offset));
    }

    [TestCase("0F2FC9")] // no memory read
    [TestCase("0F2F4A10")] // another receiver
    [TestCase("0F2F4C1110")] // indexed address
    [TestCase("0F2F6110")] // not an incoming XMM lane
    [TestCase("0F2F4908")] // object header
    [TestCase("0F2F4911")] // unaligned Single field
    [TestCase("660F2F4914")] // unaligned Double field
    [TestCase("0F2F49FC")] // negative offset
    [TestCase("640F2F4910")] // segment override
    [TestCase("F30F2F4910")] // unsupported opcode/prefix
    public void OtherMemoryShapesRemainUnproved(string bytes)
    {
        var instruction = Decode(bytes);
        Assert.That(X64FloatingFieldOperandProof.TryReadWidth(instruction, out _, out _), Is.False);
    }

    private static Instruction Decode(string bytes)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(bytes)));
        decoder.IP = 0x1000;
        return decoder.Decode();
    }
}
