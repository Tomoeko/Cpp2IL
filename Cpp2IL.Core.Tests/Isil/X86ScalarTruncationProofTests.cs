using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Iced.Intel;
using IsilRegister = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.Tests.Isil;

public class X86ScalarTruncationProofTests
{
    [TestCase("F20F2CC0C3", 32)]
    [TestCase("F2480F2CC0C3", 64)]
    public void MandatoryF2IsConsumedAndTheSignedWidthIsExplicit(string bytes, int returnBits)
    {
        var body = Decode(bytes);
        Assert.That(body[0].Mnemonic, Is.EqualTo(Mnemonic.Cvttsd2si));
        Assert.That(body[0].HasRepnePrefix, Is.False,
            "Iced's mandatory-prefix decoder consumes the F2 opcode selector.");
        var lifted = X86ScalarTruncationProof.TryLift(body, returnBits);
        Assert.That(lifted, Is.Not.Null);
        Assert.That(lifted!.Select(i => i.OpCode), Is.EqualTo(new[] { OpCode.FloatTruncateSigned, OpCode.Return }));
        Assert.That(lifted[0].Operands[1], Is.EqualTo(new IsilRegister(null, "xmm0")));
        Assert.That(lifted[0].Operands.Skip(2).Cast<Immediate>().Select(i => i.Value), Is.EqualTo(new long[] { 64, returnBits }));
        Assert.That(lifted[1].Operands[0], Is.EqualTo(lifted[0].Operands[0]));
    }

    [TestCase("F20F2CC0C3", 64)] // An EAX result does not establish a signed Int64 result.
    [TestCase("F2480F2CC0C3", 32)]
    [TestCase("F20F2CC1C3", 32)] // XMM1 is not the incoming argument.
    [TestCase("F20F2CC8C3", 32)] // Result is not EAX.
    [TestCase("F24C0F2CC0C3", 64)] // Result is R8, not RAX.
    [TestCase("F20F2C00C3", 32)] // Memory evaluation needs independent width/effect proof.
    [TestCase("F2480F2C00C3", 64)]
    [TestCase("F30F2CC0C3", 32)] // Single precision.
    [TestCase("F20F2DC0C3", 32)] // Rounding conversion, not truncation.
    [TestCase("C5FB2CC0C3", 32)] // VEX encoding is outside this first proof.
    [TestCase("F20F2CC0", 32)] // No native RET.
    [TestCase("C3", 32)]
    [TestCase("F20F2CC0C20000", 32)]
    [TestCase("F20F2CC0F3C3", 32)]
    [TestCase("F2F20F2CC0C3", 32)] // Redundant opcode prefix.
    [TestCase("66F20F2CC0C3", 32)]
    [TestCase("67F20F2CC0C3", 32)]
    [TestCase("64F20F2CC0C3", 32)]
    [TestCase("F040F20F2CC0C3", 32)]
    [TestCase("F2400F2CC0C3", 32)]
    [TestCase("90F20F2CC0C3", 32)] // Unproved entry instruction.
    [TestCase("660F57C0F20F2CC0C3", 32)] // Source overwritten before conversion.
    [TestCase("F20F2CC031C0C3", 32)] // Result overwritten.
    [TestCase("F20F2CC0F20F2CC0C3", 32)]
    [TestCase("F20F2CC08901C3", 32)] // Store after conversion.
    [TestCase("F20F2CC0E800000000C3", 32)]
    [TestCase("EB00F20F2CC0C3", 32)]
    [TestCase("F20F2CC0EB00C3", 32)]
    [TestCase("F20F2CC07400C3", 32)]
    [TestCase("F20F2CC05058C3", 32)]
    [TestCase("F20F2CC0C3", 16)]
    public void RejectsUnprovedRepresentationEffectsOrReturn(string bytes, int returnBits)
    {
        Assert.That(X86ScalarTruncationProof.TryLift(Decode(bytes), returnBits), Is.Null);
    }

    [Test]
    public void ClosedReturnCanIgnoreOnlyUnreachableSuffixBytes()
    {
        Assert.That(X86ScalarTruncationProof.TryLift(Decode("F20F2CC0C3CC488B0140B601C3"), 32), Is.Not.Null);
        Assert.That(X86ScalarTruncationProof.TryLift(Decode("7405F20F2CC0C3C3"), 32), Is.Null,
            "An alternate return cannot be discarded as a suffix.");
    }

    [Test]
    public void RejectsNon64BitDecodingMissingBytesAndExplicitOperationalPrefix()
    {
        Assert.That(X86ScalarTruncationProof.TryLift(Decode("F20F2CC0C3", 32), 32), Is.Null);
        Assert.That(X86ScalarTruncationProof.TryLift([], 32), Is.Null);
        var body = Decode("F20F2CC0C3");
        var returned = body[1];
        returned.IP++;
        body[1] = returned;
        Assert.That(X86ScalarTruncationProof.TryLift(body, 32), Is.Null);
        body = Decode("F20F2CC0C3");
        var conversion = body[0];
        conversion.HasRepnePrefix = true;
        body[0] = conversion;
        Assert.That(X86ScalarTruncationProof.TryLift(body, 32), Is.Null);
    }

    private static List<NativeInstruction> Decode(string bytes, int bitness = 64)
    {
        var data = Convert.FromHexString(bytes);
        var decoder = Decoder.Create(bitness, new ByteArrayCodeReader(data));
        var result = new List<NativeInstruction>();
        while (decoder.IP < (ulong)data.Length)
            result.Add(decoder.Decode());
        return result;
    }
}
