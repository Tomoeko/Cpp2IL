using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;
using Classification = Cpp2IL.Core.InstructionSets.X64UnwindProof.SpanClassification;
using SpanKind = Cpp2IL.Core.InstructionSets.X64UnwindProof.SpanKind;

namespace Cpp2IL.Core.Tests.Isil;

public class X86CallerExceptionRegionProofTests
{
    [TestCase("8BC183C001C3")]
    [TestCase("7501C3C3")] // both reachable returns
    [TestCase("EBFE")] // closed frame-free loop
    [TestCase("E900010000")] // direct caller-region escape; tail ABI remains independently checked
    public void FrameFreeLeafPathsNeedNoInventedUnwindEntry(string bytes)
        => Assert.That(Check(bytes, NoEntry), Is.Null);

    [Test]
    public void PlainReturnDoesNotAttributeAnAdjacentFunctionsHandlersToThisMethod()
    {
        var visited = new List<ulong>();
        var result = Check("C3CC4883EC28E800000000", (start, end) =>
        {
            visited.Add(start);
            return start == 0 ? NoEntry(start, end) : new(SpanKind.Unsupported, 1, 12);
        });
        Assert.That(result, Is.Null);
        Assert.That(visited, Is.All.EqualTo(0UL));
    }

    [TestCase("C3")]
    [TestCase("EBFE")]
    public void AnApparentlySimpleNormalPathDoesNotEraseAHiddenNativeHandler(string bytes)
        => Assert.That(Check(bytes, (start, end) => new(SpanKind.Unsupported, start, end)),
            Does.Contain("unsupported native handlers"));

    [TestCase("4883EC084883C408C3")] // a balanced explicit frame still needs unwind metadata
    [TestCase("535BC3")] // callee-saved register save/restore
    [TestCase("8BD9C3")] // EBX write also changes nonvolatile RBX
    [TestCase("660FEFF6C3")] // PXOR XMM6,XMM6
    [TestCase("E800000000C3")] // ordinary call
    [TestCase("FFD0C3")] // indirect call
    public void MissingUnwindMetadataIsNotPermissionForAFrameOrCall(string bytes)
        => Assert.That(Check(bytes, NoEntry), Does.Contain("without unwind metadata").Or.Contain("no native unwind entry"));

    [Test]
    public void OrdinaryCallsRetainTheirFallthroughWithinOneHandlerFreeRegion()
    {
        const string bytes = "4883EC28E8000000004883C428C3";
        var visited = new HashSet<ulong>();
        Assert.That(Check(bytes, (start, end) =>
        {
            visited.Add(start);
            return new(SpanKind.HandlerFree, 0, 14);
        }), Is.Null);
        Assert.That(visited, Does.Contain(9UL));
        Assert.That(visited, Does.Contain(13UL));
    }

    [Test]
    public void OnlyAProvedNonreturningCallClosesItsNativeContinuation()
    {
        const string bytes = "4883EC28E800000000CC4883EC28";
        Classification Classify(ulong start, ulong end) => start < 9
            ? new(SpanKind.HandlerFree, 0, 9) : new(SpanKind.Unsupported, 9, 14);
        Assert.That(Check(bytes, Classify, new HashSet<ulong> { 4 }), Is.Null);
        Assert.That(Check(bytes, Classify), Does.Contain("unsupported native handlers"));
    }

    [TestCase("B801000000C3")]
    [TestCase("F2E800000000C3")]
    [TestCase("FFD0C3")]
    public void AClaimedNonreturningAddressCannotHideAnUnprovedInstruction(string bytes)
        => Assert.That(Check(bytes, (_, _) => new(SpanKind.HandlerFree, 0, 32), new HashSet<ulong> { 0 }),
            Does.Contain("not a plain direct x64 call"));

    [Test]
    public void BothConditionalPathsRequireTheSameNativeRegion()
    {
        const string bytes = "85C9740483C001C3C3";
        Assert.That(Check(bytes, (start, end) => start < 8
            ? new(SpanKind.HandlerFree, 0, 8) : new(SpanKind.Unsupported, 8, 9)),
            Does.Contain("unsupported native handlers"));
    }

    [Test]
    public void DirectBranchInsideTheDecodedSpanCannotSilentlySwitchNativeFunctions()
        => Assert.That(Check("EB01CCC3", (start, end) => start < 2
                ? new(SpanKind.HandlerFree, 0, 2) : new(SpanKind.HandlerFree, 3, 4)),
            Does.Contain("unproved native unwind region boundary"));

    [TestCase("7501C3")] // conditional target at unproved byte-span end
    [TestCase("FFE0")] // indirect exit
    [TestCase("EBFF")] // middle of an instruction
    [TestCase("90")] // fallthrough without a terminator
    [TestCase("C20000")] // a different return convention
    [TestCase("0F")] // invalid/truncated reachable decoding
    public void AmbiguousEdgesAndBoundariesRemainUnproved(string bytes)
        => Assert.That(Check(bytes, NoEntry), Is.Not.Null);

    [Test]
    public void ManagedEntryMustMatchTheNativeFunctionsEntry()
    {
        var body = Decode("C3", 0x100);
        Assert.That(X86CallerExceptionRegionProof.Check(body, 0x100, new HashSet<ulong>(),
            (_, _) => new(SpanKind.HandlerFree, 0xF0, 0x110)), Does.Contain("inside a different native unwind region"));
    }

    [Test]
    public void DecoderGapsAndWrongBitnessCannotEstablishCoverage()
    {
        var body = Decode("90C3");
        var instruction = body[1];
        instruction.IP++;
        body[1] = instruction;
        Assert.That(X86CallerExceptionRegionProof.Check(body, 0, new HashSet<ulong>(), NoEntry),
            Does.Contain("not contiguous"));
        Assert.That(X86CallerExceptionRegionProof.Check(Decode("C3", 0, 32), 0, new HashSet<ulong>(), NoEntry),
            Does.Contain("no valid x64 decoding"));
    }

    private static string? Check(string bytes, Func<ulong, ulong, Classification> classify, ISet<ulong>? noReturn = null)
        => X86CallerExceptionRegionProof.Check(Decode(bytes), 0, noReturn ?? new HashSet<ulong>(), classify);

    private static Classification NoEntry(ulong start, ulong end) => new(SpanKind.NoEntry, start, end);

    private static List<Instruction> Decode(string bytes, ulong start = 0, int bitness = 64)
    {
        var data = Convert.FromHexString(bytes);
        var decoder = Decoder.Create(bitness, new ByteArrayCodeReader(data), start);
        var result = new List<Instruction>();
        while (decoder.IP - start < (ulong)data.Length)
            result.Add(decoder.Decode());
        return result;
    }
}
