using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86NonvolatileXmmStackProofTests
{
    private const ulong Entry = 0x180001000;
    private const string OnePair =
        "4883EC38 0F29742420 0F28F1 E800000000 F30F58C6 0F28742420 4883C438 C3";
    private static readonly byte[] OnePairUnwind = [9, 0x68, 2, 0, 4, 0x62];
    private const string ThreePairs =
        "4883EC58 0F29742440 0F297C2430 440F29442420 E800000000 " +
        "440F28442420 0F287C2430 0F28742440 4883C458 C3";
    private static readonly byte[] ThreePairUnwind =
        [20, 0x88, 2, 0, 14, 0x78, 3, 0, 9, 0x68, 4, 0, 4, 0xA2];

    [Test]
    public void ExactUnwindAndDisjointSlotsPermitOnlyTheAbiSaveRestoreOperations()
    {
        var one = Decode(OnePair);
        Assert.That(X86NonvolatileXmmStackProof.Prove(one, Entry,
            Index(one, 9, OnePairUnwind), _ => true),
            Is.EquivalentTo(new[] { one[1].IP, one[5].IP }));

        var three = Decode(ThreePairs);
        Assert.That(X86NonvolatileXmmStackProof.Prove(three, Entry,
            Index(three, 20, ThreePairUnwind), _ => true),
            Is.EquivalentTo(new[]
            {
                three[1].IP, three[2].IP, three[3].IP,
                three[5].IP, three[6].IP, three[7].IP,
            }));
    }

    [TestCase("4889442428")] // An eight-byte write overlaps half the saved XMM slot.
    [TestCase("488D4C2420")] // A computed address could escape to the callee.
    [TestCase("488BCC")] // Copying RSP into an argument register can escape the frame.
    [TestCase("4883EC104883C410")] // RSP moves away and back between save and restore.
    [TestCase("EB00")] // A branch defeats the proved straight-line path.
    [TestCase("C3")] // An early return bypasses the restore.
    public void RejectsSlotAliasEscapesUnstableStackAndControlFlow(string inserted)
    {
        var body = Decode(OnePair.Replace("0F28742420", inserted + " 0F28742420"));
        Assert.That(X86NonvolatileXmmStackProof.Prove(body, Entry,
            Index(body, 9, OnePairUnwind), _ => true), Is.Empty);
    }

    [Test]
    public void RejectsUnboundCallsAndMismatchedOrAbsentUnwindSaves()
    {
        var body = Decode(OnePair);
        Assert.That(X86NonvolatileXmmStackProof.Prove(body, Entry,
            Index(body, 9, OnePairUnwind), _ => false), Is.Empty);
        Assert.That(X86NonvolatileXmmStackProof.Prove(body, Entry,
            Index(body, 9, [4, 0x62]), _ => true), Is.Empty);
        Assert.That(X86NonvolatileXmmStackProof.Prove(body, Entry,
            Index(body, 9, [9, 0x78, 2, 0, 4, 0x62]), _ => true), Is.Empty);
        Assert.That(X86NonvolatileXmmStackProof.Prove(body, Entry,
            Index(body, 9, [9, 0x68, 3, 0, 4, 0x62]), _ => true), Is.Empty);
        Assert.That(X86NonvolatileXmmStackProof.Prove(body, Entry,
            Index(body, 9, [9, 0x68, 2, 0, 4, 0x52]), _ => true), Is.Empty);
    }

    [Test]
    public void RejectsMismatchedRestoreOrVolatileRegister()
    {
        var mismatch = Decode(OnePair.Replace("0F28742420", "0F287C2420"));
        Assert.That(X86NonvolatileXmmStackProof.Prove(mismatch, Entry,
            Index(mismatch, 9, OnePairUnwind), _ => true), Is.Empty);

        var volatilePair = Decode(OnePair.Replace("0F29742420", "0F29442420")
            .Replace("0F28742420", "0F28442420"));
        Assert.That(X86NonvolatileXmmStackProof.Prove(volatilePair, Entry,
            Index(volatilePair, 9, OnePairUnwind), _ => true), Is.Empty);
    }

    [Test]
    public void SaveMustRemainInsideTheAllocatedFrame()
    {
        var body = Decode(OnePair.Replace("4883EC38", "4883EC18")
            .Replace("4883C438", "4883C418"));
        Assert.That(X86NonvolatileXmmStackProof.Prove(body, Entry,
            Index(body, 9, [9, 0x68, 2, 0, 4, 0x22]), _ => true), Is.Empty);
    }

    [Test]
    public void RejectsDecodedReturnBeforeUnwindCoveredFunctionEnd()
    {
        var body = Decode(OnePair);
        var index = new X64UnwindProof.Index(0x180000000, 0x2000,
            [new X64UnwindProof.Section(0x1000, 0x1000, 0, 0x1000, 0x60000020)],
            [new X64UnwindProof.Function(0x1000,
                checked((uint)(body[^1].NextIP - 0x180000000 + 1)),
                new X64UnwindProof.Unwind(9, 0, OnePairUnwind), 0x2000, 0x1000)]);
        Assert.That(X86NonvolatileXmmStackProof.Prove(body, Entry, index, _ => true), Is.Empty);
    }

    private static List<Instruction> Decode(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", string.Empty));
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), Entry);
        var body = new List<Instruction>();
        while (decoder.IP < Entry + (ulong)bytes.Length)
            body.Add(decoder.Decode());
        return body;
    }

    private static X64UnwindProof.Index Index(IReadOnlyList<Instruction> body,
        byte prolog, byte[] codes) => new(0x180000000, 0x2000,
        [new X64UnwindProof.Section(0x1000, 0x1000, 0, 0x1000, 0x60000020)],
        [new X64UnwindProof.Function(0x1000,
            checked((uint)(body[^1].NextIP - 0x180000000)),
            new X64UnwindProof.Unwind(prolog, 0, codes), 0x2000, 0x1000)]);
}
