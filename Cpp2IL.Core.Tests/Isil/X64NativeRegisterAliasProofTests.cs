using System;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64NativeRegisterAliasProofTests
{
    [TestCase("488BD9E800000000C74310FFFFFFFFC3", Register.RBX)]
    [TestCase("488BD94C8BE34C89E3C74310FFFFFFFFC3", Register.RBX)]
    [TestCase("4885D27405488BD9EB03488BD9C74310FFFFFFFFC3", Register.RBX)]
    [TestCase("C74110FFFFFFFFC3", Register.RCX)]
    public void IncomingPointerSurvivesOnlyUnchangedFullWidthCopies(string hex, Register receiver)
        => Assert.That(Proves(hex, receiver), Is.True);

    [TestCase("488BD9BB01000000C74310FFFFFFFFC3", Register.RBX)]
    [TestCase("488BD9B301C74310FFFFFFFFC3", Register.RBX)]
    [TestCase("488BD94883C301C74310FFFFFFFFC3", Register.RBX)]
    [TestCase("E800000000C74110FFFFFFFFC3", Register.RCX)]
    [TestCase("4885D27405488BD9EB03488BDAC74310FFFFFFFFC3", Register.RBX)]
    [TestCase("488BD94885D275FBC74310FFFFFFFFC3", Register.RBX)]
    [TestCase("48894C2408488B5C2408C74310FFFFFFFFC3", Register.RBX)]
    [TestCase("488BE1C7442410FFFFFFFFC3", Register.RSP)]
    public void PartialWritesCallsDivergentPathsLoopsAndSpillsRemainUnproved(string hex, Register receiver)
        => Assert.That(Proves(hex, receiver), Is.False);

    [Test]
    public void CapturedCallResultBindsItsOwnProducerAcrossLaterCalls()
    {
        const string body = "E800000000488BD8E800000000C74310FFFFFFFFC3";
        Assert.That(Proves(body, Register.RBX, 0x1000), Is.True);
        Assert.That(Proves(body, Register.RBX, 0x1008), Is.False);
        Assert.That(Proves(body, Register.RBX, 0x1001), Is.False);
        Assert.That(Proves("E800000000E800000000C74010FFFFFFFFC3", Register.RAX, 0x1000), Is.False);
    }

    [Test]
    public void DisconnectedCodeCannotSupplyAnEntryPointer()
        => Assert.That(Proves("C3488BD9C74310FFFFFFFFC3", Register.RBX), Is.False);

    [Test]
    public void MissingDecodeIntervalsAndExternalBranchesFailClosed()
    {
        var body = X86Utils.Disassemble(Convert.FromHexString("488BD9C74310FFFFFFFFC3"), 0x1000, false).ToArray();
        var changed = body[1];
        changed.IP++;
        body[1] = changed;
        Assert.That(X64NativeRegisterAliasProof.IsAlias(body, changed.IP, Register.RBX, Register.RCX), Is.False);
        Assert.That(Proves("E900000001C74110FFFFFFFFC3", Register.RCX), Is.False);
    }

    private static bool Proves(string hex, Register receiver, ulong? call = null)
    {
        var body = X86Utils.Disassemble(Convert.FromHexString(hex), 0x1000, false).ToArray();
        return X64NativeRegisterAliasProof.IsAlias(body, body[^2].IP, receiver, Register.RCX, call);
    }
}
