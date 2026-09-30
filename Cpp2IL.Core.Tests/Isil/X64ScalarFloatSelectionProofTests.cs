using System;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ScalarFloatSelectionProofTests
{
    [TestCase("F30F5DC1")]
    [TestCase("F30F5FC1")]
    [TestCase("F20F5DC1")]
    [TestCase("F20F5FC1")]
    public void ScalarSelectionsRequireCompleteIndependentCallerEvidence(string hex)
    {
        var body = Decode(hex + "C3");
        Assert.That(X64ScalarFloatSelectionProof.IsScalarBody(body, body[0].IP), Is.True);
        Assert.That(X64ScalarFloatSelectionProof.CanLift(null, body[0]), Is.False);
        Assert.That(new X86InstructionSet().GetIsilFromInstruction(body[0]).Single().OpCode,
            Is.EqualTo(Cpp2IL.Core.ISIL.OpCode.NotImplemented));
    }

    [TestCase("F30F5F01C3")] // Memory source needs read/fault provenance.
    [TestCase("F30F5FC1E800000000C3")] // Upper lanes cross an unproved callee ABI.
    [TestCase("F30F5FC10F1101C3")] // Packed memory store observes preserved upper lanes.
    [TestCase("F30F5FC10F58C1C3")] // Packed arithmetic observes upper lanes.
    [TestCase("F30F5FC1660F7EC0C3")] // Bit reinterpretation is outside scalar floating typing.
    [TestCase("F30F5FF1C3")] // Nonvolatile vector register requires an independent spill proof.
    [TestCase("F30F5FC10FAEC8C3")] // MXCSR mutation is observable control state.
    [TestCase("F30F5FC1EB7FC3")] // Unproved branch exit.
    [TestCase("F30F5FC1F20F1101C3")] // Binary64 store observes bits32..63 preserved by MINSS/MAXSS.
    [TestCase("F30F5FC1F20F58C1C3")] // Binary64 arithmetic observes preserved upper bits.
    [TestCase("F20F5FC1F30F10C1C3")] // Mixed scalar widths need lane provenance.
    [TestCase("F30F5FC10FAE08C3")] // FXRSTOR can restore control state without a vector operand.
    [TestCase("F30F5FC10F22C0C3")] // MOV CR0,RAX changes control state without naming a vector register.
    public void ScalarSelectionsRejectUnprovedLaneOrControlState(string hex)
    {
        var body = Decode(hex);
        Assert.That(X64ScalarFloatSelectionProof.IsScalarBody(body, body[0].IP), Is.False);
    }

    [TestCase("F30F5FC1C3CCCC", true)]
    [TestCase("F30F5FC1CC", false)]
    [TestCase("F30F5FC1CCC3", false)]
    [TestCase("F30F5FC17401C3CC", false)]
    public void ScalarShapeExcludesOnlyUnreachableTerminalTrapPadding(string hex, bool accepted)
    {
        var body = Decode(hex);
        Assert.That(X64ScalarFloatSelectionProof.IsScalarBody(body, body[0].IP), Is.EqualTo(accepted));
    }

    [Test]
    public void ProvedNonreturningCallCanPrecedeAuthenticatedTrapPadding()
    {
        var body = Decode("F30F5FC1E800000000CC");
        Assert.That(X64ScalarFloatSelectionProof.IsScalarBody(body, body[0].IP,
            new System.Collections.Generic.HashSet<ulong> { body[1].IP }), Is.True);
        Assert.That(X64ScalarFloatSelectionProof.IsScalarBody(body, body[0].IP), Is.False);
    }

    [TestCase("0F2801", false)] // Packed memory reads are not scalar register projections.
    [TestCase("0F2901", false)] // Packed memory stores expose the full vector.
    [TestCase("0F28F0", false)] // XMM6 requires an independent save/restore proof.
    [TestCase("0F57C1", false)] // XOR of distinct values is not a proved scalar zero.
    [TestCase("F00F28C1", false)] // Invalid LOCK cannot reach native authentication.
    [TestCase("0F28C1", true)]
    [TestCase("660F28C1", true)]
    [TestCase("0F57C0", true)]
    [TestCase("660F57C0", true)]
    [TestCase("F30F1101", true)] // Scalar field-store proofs still own memory provenance.
    [TestCase("F20F1101", true)]
    public void ProjectionPreflightRejectsOnlyImpossibleSites(string hex, bool possible)
    {
        var site = Decode(hex)[0];
        Assert.That(X64ScalarFloatSelectionProof.IsPossibleProjectionSite(site), Is.EqualTo(possible));
    }

    private static Instruction[] Decode(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), 0x1000);
        return decoder.ToArray();
    }
}
