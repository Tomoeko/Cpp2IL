using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86BooleanReturnSelfTestProofTests
{
    private const string BranchOnBooleanReturn = "E81B00000084C07406B801000000C331C0C3";

    [Test]
    public void AdmitsOnlyAnAdjacentLowByteSelfTestWithZeroFlagBranches()
    {
        var body = Decode(BranchOnBooleanReturn);
        Assert.That(X86BooleanReturnSelfTestProof.IsAdjacentShape(body, 1), Is.True);
        Assert.That(X86BooleanReturnSelfTestProof.HasIncomingBranch(body, body[1].IP, body[2].NextIP), Is.False);
        Assert.That(X86BooleanReturnSelfTestProof.OnlyZeroFlagConsumed(body, 1), Is.True);

        var wideTest = Decode("E81B00000085C07406B801000000C331C0C3");
        Assert.That(X86BooleanReturnSelfTestProof.IsAdjacentShape(wideTest, 1), Is.False);
        var interrupted = Decode("E81B00000088C884C07406B801000000C331C0C3");
        Assert.That(X86BooleanReturnSelfTestProof.IsAdjacentShape(interrupted, 2), Is.False);
    }

    [TestCase("E81B00000084C074040F9AC3C331C0C3")] // SETP consumes byte parity.
    [TestCase("E81B00000084C074040F92C3C331C0C3")] // SETB consumes carry.
    [TestCase("E81B00000084C074029FC331C0C3")] // LAHF observes undefined AF.
    public void RejectsOtherLiveFlagConsumers(string bytes)
    {
        var body = Decode(bytes);
        Assert.That(X86BooleanReturnSelfTestProof.IsAdjacentShape(body, 1), Is.True);
        Assert.That(X86BooleanReturnSelfTestProof.OnlyZeroFlagConsumed(body, 1), Is.False);
    }

    [Test]
    public void RejectsAlternateEntryAfterTheProvedCall()
    {
        var body = Decode(BranchOnBooleanReturn + "EBF1");
        Assert.That(X86BooleanReturnSelfTestProof.HasIncomingBranch(body, body[1].IP, body[2].NextIP), Is.True);
    }

    [Test]
    public void AdmitsFlagPreservingMovesBeforeRegisterConditionalMove()
    {
        var body = Decode("E81B00000084C0B911000000BAEFFFFFFF0F44CA8BC1C3");
        Assert.That(X86BooleanReturnSelfTestProof.FindConsumerIndex(body, 1), Is.EqualTo(4));
        Assert.That(X86BooleanReturnSelfTestProof.HasIncomingBranch(body, body[1].IP,
            body[4].NextIP), Is.False);
        Assert.That(X86BooleanReturnSelfTestProof.OnlyZeroFlagConsumed(body, 1), Is.True);
    }

    [Test]
    public void RejectsMemoryConditionalMoveAndFlagModifyingGap()
    {
        var memorySource = Decode("E81B00000084C00F4401C3");
        Assert.That(X86BooleanReturnSelfTestProof.FindConsumerIndex(memorySource, 1), Is.EqualTo(-1));

        var flagModifying = Decode("E81B00000084C083C0010F44CA8BC1C3");
        Assert.That(X86BooleanReturnSelfTestProof.FindConsumerIndex(flagModifying, 1), Is.EqualTo(-1));
    }

    private static List<Instruction> Decode(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
        var body = new List<Instruction>();
        while (decoder.IP < (ulong)bytes.Length)
            body.Add(decoder.Decode());
        return body;
    }
}
