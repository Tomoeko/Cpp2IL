using System;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64IntegerTruncationProofTests
{
    [TestCase("8BC1C3")]
    [TestCase("9089C8C3")]
    [TestCase("488BC148C1F8208BC0C3")]
    [TestCase("48C1E9208BC1C3")]
    [TestCase("89511048C1FA20895114C3")]
    [TestCase("89511048C1EA20895114C3")]
    public void CompleteLowHighAndSplitBodiesRetainAnExplicitLow32Transfer(string hex)
        => Assert.That(X64IntegerTruncationProof.IsClosedBody(Decode(hex)), Is.True);

    [TestCase("8BC1E800000000C3")] // Call effects.
    [TestCase("8BC1EB00C3")]
    [TestCase("8BC17400C3")]
    [TestCase("8B4110C3")] // A memory read is not a register truncation.
    [TestCase("488B41108BC0C3")]
    [TestCase("48895110C3")] // An eight-byte store cannot become stfld.i4.
    [TestCase("8BD98BC3C3")] // Unpreserved nonvolatile writes.
    [TestCase("488BD98BC3C3")]
    [TestCase("448BE18BC1C3")]
    [TestCase("8BE18BC1C3")] // Stack-pointer write.
    [TestCase("668BC1C3")]
    [TestCase("8AC1C3")]
    [TestCase("C1E9208BC1C3")]
    [TestCase("48C1E9008BC1C3")]
    [TestCase("48C1E9408BC1C3")]
    [TestCase("64895110C3")]
    [TestCase("F389C8C3")]
    [TestCase("8BC1C20800")]
    [TestCase("8BC1C3C3")]
    [TestCase("8BC1")]
    public void EffectsOtherWidthsClobberedAbiAndIncompleteExitsRemainUnproved(string hex)
        => Assert.That(X64IntegerTruncationProof.IsClosedBody(Decode(hex)), Is.False);

    [TestCase("8BC1", Register.RCX, Register.RAX, Register.None, 0)]
    [TestCase("89C8", Register.RCX, Register.RAX, Register.None, 0)]
    [TestCase("895110", Register.RDX, Register.None, Register.RCX, 16)]
    [TestCase("899100100000", Register.RDX, Register.None, Register.RCX, 4096)]
    public void TransferSitesKeepTheFullSourceParentAndExactStoreWidth(string hex,
        Register source, Register destination, Register receiver, int offset)
    {
        var site = X64IntegerTruncationProof.TryGetSite(Decode(hex).Single());
        Assert.That(site, Is.EqualTo(new X64IntegerTruncationProof.Site(0x1000, source, destination, receiver, offset)));
    }

    [TestCase("488BC1")]
    [TestCase("668BC1")]
    [TestCase("8AC1")]
    [TestCase("8B4110")]
    [TestCase("89541110")]
    [TestCase("89510F")]
    [TestCase("899101100000")]
    [TestCase("48895110")]
    [TestCase("89542410")]
    [TestCase("67895110")]
    [TestCase("F389C8")]
    public void ATransferSiteCannotGuessWidthsAddressesOrPrefixMeaning(string hex)
        => Assert.That(X64IntegerTruncationProof.TryGetSite(Decode(hex).Single()), Is.Null);

    [TestCase("48C1E820C3", 0)]
    [TestCase("48C1F83FC3", 0)]
    [TestCase("48C1E920488BD1488BC2C3", 0)]
    [TestCase("48C1E9204885D27405488BC1EB03488BC1C3", 0)]
    public void AFullShiftResultMustReachEveryNativeEntryPathToItsUse(string hex, int definition)
    {
        var body = Decode(hex);
        Assert.That(X64NativeRegisterAliasProof.IsAliasFromIntegerShift(body, body[^1].IP,
            Register.RAX, body[definition].IP), Is.True);
    }

    [TestCase("48C1E820B001C3", 0)]
    [TestCase("48C1E820B801000000C3", 0)]
    [TestCase("48C1E820E800000000C3", 0)]
    [TestCase("4885D2740648C1E820EB029090C3", 2)]
    [TestCase("48C1E800C3", 0)]
    [TestCase("48C1E840C3", 0)]
    [TestCase("48D3E8C3", 0)]
    [TestCase("C1E820C3", 0)]
    public void PartialClobbersCallsBypassesAndUnprovedShiftCountsLoseTheFullResult(string hex, int definition)
    {
        var body = Decode(hex);
        Assert.That(X64NativeRegisterAliasProof.IsAliasFromIntegerShift(body, body[^1].IP,
            Register.RAX, body[definition].IP), Is.False);
    }

    [TestCase("8BC1C3", 0)]
    [TestCase("8BC14C8BC0498BC0C3", 0)]
    [TestCase("8BC14885C97405488BD0EB03488BD0488BC2C3", 0)]
    public void ARegisterTruncationDefinesTheZeroFilledFullParentAcross64BitCopies(string hex, int definition)
    {
        var body = Decode(hex);
        Assert.That(X64NativeRegisterAliasProof.IsAliasFromIntegerTruncation(body, body[^1].IP,
            Register.RAX, body[definition].IP), Is.True);
    }

    [TestCase("8BC1B001C3", 0)]
    [TestCase("8BC1B801000000C3", 0)]
    [TestCase("8BC1E800000000C3", 0)]
    [TestCase("4885D274048BC1EB029090C3", 2)]
    [TestCase("488BC1C3", 0)]
    [TestCase("894110C3", 0)]
    [TestCase("668BC1C3", 0)]
    public void AParentValueCannotSurvivePartialOverwritesOrInventA32BitDefinition(string hex, int definition)
    {
        var body = Decode(hex);
        Assert.That(X64NativeRegisterAliasProof.IsAliasFromIntegerTruncation(body, body[^1].IP,
            Register.RAX, body[definition].IP), Is.False);
    }

    [Test]
    public void DefinitionsMustHaveAnExactDecodedBoundaryAndContinuousPath()
    {
        var truncated = Decode("8BC1C3");
        var shifted = Decode("48C1E820C3");
        Assert.Multiple(() =>
        {
            Assert.That(X64NativeRegisterAliasProof.IsAliasFromIntegerTruncation(truncated,
                truncated[^1].IP, Register.RAX, 0x1001), Is.False);
            Assert.That(X64NativeRegisterAliasProof.IsAliasFromIntegerShift(shifted,
                shifted[^1].IP, Register.RAX, 0x1001), Is.False);
        });
        var gap = shifted[^1];
        gap.IP++;
        shifted[^1] = gap;
        Assert.That(X64NativeRegisterAliasProof.IsAliasFromIntegerShift(shifted,
            gap.IP, Register.RAX, shifted[0].IP), Is.False);
        Assert.That(X64IntegerTruncationProof.IsClosedBody(truncated), Is.True);
        gap = truncated[^1];
        gap.IP++;
        truncated[^1] = gap;
        Assert.That(X64NativeRegisterAliasProof.IsAliasFromIntegerTruncation(truncated,
            gap.IP, Register.RAX, truncated[0].IP), Is.False);
        Assert.That(X64IntegerTruncationProof.IsClosedBody(truncated), Is.False);
    }

    private static Instruction[] Decode(string hex)
        => X86Utils.Disassemble(Convert.FromHexString(hex), 0x1000, false).ToArray();
}
