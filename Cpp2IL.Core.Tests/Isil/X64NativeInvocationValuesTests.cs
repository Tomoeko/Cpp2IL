using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64NativeInvocationValuesTests
{
    [TestCase("0FB6DAE8000000000FB6D3C3", 8, true)] // Save DL in EBX across a call, then restore BL.
    [TestCase("0FB6DAE8000000000FB6D3C3", 32, false)] // The incoming value only establishes eight bits.
    [TestCase("89D3E80000000089DAC3", 32, true)]
    [TestCase("89D3E80000000089DAC3", 64, false)]
    [TestCase("88D3E80000000088DAC3", 8, true)]
    [TestCase("88D3E80000000088DAC3", 32, false)]
    [TestCase("6689D3E8000000006689DAC3", 16, true)]
    [TestCase("6689D3E8000000006689DAC3", 32, false)]
    [TestCase("4863DAE80000000089DAC3", 32, true)] // Native sign extension retains the original low 32 bits.
    [TestCase("4863DAE80000000089DAC3", 64, false)]
    [TestCase("89D6E80000000089F2C3", 32, true)]
    [TestCase("E800000000C3", 8, false)] // Volatile incoming parameter was not captured.
    [TestCase("88E2C3", 8, false)] // A high-byte register is a different value.
    public void ScalarArgumentsRequireTheirActualWidthAndCallCapture(string hex, int bits, bool expected)
    {
        var body = Decode(hex);
        var values = X64NativeInvocationValues.Create(body, new HashSet<ulong>());
        Assert.That(values, Is.Not.Null);
        Assert.That(values!.Matches(body[^1].IP, Register.RDX, bits, new(Register.RDX)), Is.EqualTo(expected));
    }

    [TestCase("31D2C3", true)]
    [TestCase("B200C3", false)] // DL==0 does not establish a null pointer.
    [TestCase("66BA0000C3", false)]
    [TestCase("BA00000000C3", true)]
    [TestCase("BA01000000C3", false)]
    [TestCase("48C7C200000000C3", true)]
    public void HiddenMethodInfoNeedsAllSixtyFourBits(string hex, bool expected)
    {
        var body = Decode(hex);
        Assert.That(X64NativeInvocationValues.Create(body, new HashSet<ulong>())!
            .Matches(body[^1].IP, Register.RDX, 64, new(Register.None, Literal: 0)), Is.EqualTo(expected));
    }

    [TestCase("4531C0418D50F9E97F000000", true)]
    [TestCase("4531C0418D50F8E97F000000", false)] // Different signed displacement.
    [TestCase("41B801000000418D50F9E97F000000", false)] // Base is nonzero.
    public void SignedLiteralLeaNeedsAnIndependentZeroBase(string hex, bool expected)
    {
        var body = Decode(hex);
        Assert.That(X64NativeInvocationValues.Create(body, new HashSet<ulong>())!
            .Matches(body[^1].IP, Register.RDX, 32, new(Register.None, Literal: unchecked((ulong)-7L))),
            Is.EqualTo(expected));
    }

    [Test]
    public void EveryNativePredecessorMustSupplyTheSameValue()
    {
        var body = Decode("85C9740489D3EB02B30789DAC3");
        Assert.That(X64NativeInvocationValues.Create(body, new HashSet<ulong>())!
            .Matches(body[^1].IP, Register.RDX, 8, new(Register.RDX)), Is.False);
        var cycle = Decode("89D3EBFE89DAC3");
        Assert.That(X64NativeInvocationValues.Create(cycle, new HashSet<ulong>())!
            .Matches(cycle[^1].IP, Register.RDX, 32, new(Register.RDX)), Is.False);
    }

    [TestCase("6489DAC3")] // Segment override, even on a register operand.
    [TestCase("F089DAC3")] // Invalid LOCK register operation.
    [TestCase("7501C3")] // Conditional edge leaves the authenticated body.
    [TestCase("FF20")] // Indirect exit.
    public void UnauthenticatedControlFlowAndPrefixesReject(string hex)
        => Assert.That(X64NativeInvocationValues.Create(Decode(hex), new HashSet<ulong>()), Is.Null);

    [TestCase("4883EC28E87F000000", false, true)]
    [TestCase("4883EC20E87F000000", false, false)] // Shadow space exists, but alignment is wrong.
    [TestCase("E87F000000", false, false)]
    [TestCase("534883EC20E87F000000", false, true)]
    [TestCase("535556574154E87F000000", false, false)] // Saved registers are not outgoing shadow space.
    [TestCase("4883EC2053E87F000000", false, false)] // A push occupies the top of a prior allocation.
    [TestCase("4883EC284883C428E97F000000", true, true)]
    [TestCase("4883EC28E97F000000", true, false)]
    [TestCase("534883EC204883C420E97F000000", true, false)] // Saved register still occupies the stack.
    public void CallAndTailFramesRequireShadowSpaceAlignmentAndBalancedEntryStack(string hex, bool tail, bool expected)
    {
        var body = Decode(hex + (tail ? "" : "C3"));
        var site = tail ? body[^1].IP : body[^2].IP;
        Assert.That(X64NativeInvocationValues.Create(body, new HashSet<ulong>())!
            .HasCallFrame(site, tail), Is.EqualTo(expected));
    }

    [Test]
    public void RepeatedBranchMergesShareFramesAcrossCallsites()
    {
        var body = Decode("4883EC28" + string.Concat(Enumerable.Repeat("85C9740390EB0190", 60)) +
                          "E87F0000004883C428C3");
        var values = X64NativeInvocationValues.Create(body, new HashSet<ulong>());
        Assert.That(values, Is.Not.Null);
        Assert.That(values!.HasCallFrame(body[^3].IP, false), Is.True);
        Assert.That(values.HasCallFrame(body[^1].IP, true), Is.True);
        Assert.That(values.HasCallFrame(body[^3].IP, true), Is.False,
            "A cached frame still applies the current call or tail convention.");
    }

    [TestCase("4883EC2885C974044883EC10E87F000000C3")] // Different allocations reach the call.
    [TestCase("4883EC2885C975FCE87F000000C3")] // A loop has no proved stack recurrence.
    public void SharedFramesKeepConflictingAndCyclicDefinitionsUnproved(string hex)
    {
        var body = Decode(hex);
        var values = X64NativeInvocationValues.Create(body, new HashSet<ulong>());
        Assert.That(values, Is.Not.Null);
        Assert.That(values!.HasCallFrame(body[^2].IP, false), Is.False);
        Assert.That(values.HasCallFrame(body[^2].IP, false), Is.False,
            "Caching a rejected merge cannot promote it on a later query.");
    }

    private static Instruction[] Decode(string hex) => Decoder.Create(64,
        new ByteArrayCodeReader(Convert.FromHexString(hex)), 0x1000).ToArray();
}
