using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64NativeInvocationFrameProofTests
{
    [TestCase("4883EC28E87F0000004883C428C3", 4, "0442", true)]
    [TestCase("534883EC20E87F0000004883C4205BC3", 5, "05320130", true)]
    [TestCase("48895C2408574883EC20E87F000000488B5C24304883C4205FC3", 10, "0A3406000A320670", true)]
    [TestCase("4883EC284883C428E97F000000", 4, "0442", true)]
    [TestCase("534883EC20E87F0000004883C4205FC3", 5, "05320130", false)] // Wrong restored register.
    [TestCase("4883EC28E87F0000004883C420C3", 4, "0442", false)] // Entry stack is not restored.
    [TestCase("4883EC284889CBE87F0000004883C428C3", 4, "0442", false)] // Unsaved nonvolatile write.
    [TestCase("534883EC2048894C2420E87F0000004883C4205BC3", 5, "05320130", false)] // Overwritten saved slot.
    [TestCase("4883EC28E87F000000EB044883C428C3", 4, "0442", false)] // Restore bypassed by native control flow.
    [TestCase("48895C2408574883EC20E87F000000488B5C24384883C4205FC3", 10, "0A3406000A320670", false)] // Wrong home slot.
    [TestCase("4883EC280F28F0E87F0000004883C428C3", 4, "0442", false)] // Unsaved XMM6 write.
    [TestCase("4883EC28E87F0000004883C428C3", 4, "0432", false)] // Unwind allocation disagrees with instructions.
    [TestCase("4883EC28E87F0000004883C428C3", 3, "0442", false)] // Prolog extent disagrees.
    public void NativeSaveAllocationAndEveryNormalExitMustMatchExactUnwind(string hex, int prolog,
        string unwindHex, bool expected)
    {
        var body = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(hex)), 0x1000).ToArray();
        var values = X64NativeInvocationValues.Create(body, new HashSet<ulong>());
        Assert.That(values, Is.Not.Null);
        Assert.That(X64NativeInvocationFrameProof.IsValid(body, values!, (length, codes) =>
            length == prolog && codes.SequenceEqual(Convert.FromHexString(unwindHex))), Is.EqualTo(expected));
    }

    [Test]
    public void NonReturningBranchesCannotAlterTheAuthenticatedFrame()
    {
        var body = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(
            "4883EC2885C974054883C428C34883EC10E87F000000CC")), 0x1000).ToArray();
        var helper = body.Single(instruction => instruction.Code == Code.Call_rel32_64);
        var values = X64NativeInvocationValues.Create(body, new HashSet<ulong> { helper.IP });
        Assert.That(values, Is.Not.Null);
        Assert.That(X64NativeInvocationFrameProof.IsValid(body, values!, (length, codes) =>
            length == 4 && codes.SequenceEqual(new byte[] { 4, 0x42 })), Is.False);
    }
}
