using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;
using Classification = Cpp2IL.Core.InstructionSets.X64UnwindProof.SpanClassification;
using SpanKind = Cpp2IL.Core.InstructionSets.X64UnwindProof.SpanKind;

namespace Cpp2IL.Core.Tests.Isil;

public class X86TerminalBoundsThrowDiagnosticTests
{
    [Test]
    public void ExactReachableTerminalDirectCallGetsAnUnresolvedDiagnosis()
    {
        // A real helper proof is injected as a Boolean fact. The diagnostic still
        // cannot mark this method recovered or emit a managed throw.
        var body = Decode("4883EC28E800000000");
        Assert.That(Check(body, HandlerFree, _ => true),
            Is.EqualTo(X86TerminalBoundsThrowDiagnostic.UnresolvedBoundsSemantics));
    }

    [TestCase(false)] // unknown or returning target
    [TestCase(true)]  // known target, but no native unwind proof
    public void UnknownTargetOrInvalidUnwindKeepsTheOriginalFailure(bool knownTarget)
    {
        var body = Decode("4883EC28E800000000");
        Func<ulong, ulong, Classification> classify = knownTarget ? Unsupported : HandlerFree;
        Assert.That(Check(body, classify, _ => knownTarget), Is.Null);
    }

    [Test]
    public void AReachableInstructionAfterTheCallIsNotTerminal()
    {
        var body = Decode("4883EC28E800000000C3");
        Assert.That(Check(body, HandlerFree, _ => true), Is.Null);
    }

    [TestCase("7505E800000000")] // alternate conditional edge reaches the raw end
    [TestCase("EBFFE800000000")] // alternate branch lands inside an instruction
    public void AnAlternateUnprovedEdgeCannotBeHiddenByTheTerminalCall(string bytes)
    {
        var body = Decode(bytes);
        Assert.That(Check(body, HandlerFree, _ => true), Is.Null);
    }

    [Test]
    public void UnprovedOrIndirectCallsCannotReceiveTheTerminalDiagnosis()
    {
        Assert.That(Check(Decode("FFD0"), HandlerFree, _ => true), Is.Null);
        Assert.That(Check(Decode("E800000000"), HandlerFree, _ => false), Is.Null);
    }

    private static string? Check(IReadOnlyList<Instruction> body,
        Func<ulong, ulong, Classification> classify, Func<ulong, bool> prove)
    {
        var alreadyProved = new HashSet<ulong>();
        var originalFailure = X86CallerExceptionRegionProof.Check(body, 0, alreadyProved, classify);
        return originalFailure == null ? null :
            X86TerminalBoundsThrowDiagnostic.TryClassify(body, 0, body[^1].NextIP,
                alreadyProved, originalFailure, classify, prove);
    }

    private static Classification HandlerFree(ulong start, ulong end)
        => new(SpanKind.HandlerFree, 0, 32, 0);

    private static Classification Unsupported(ulong start, ulong end)
        => new(SpanKind.Unsupported, 0, 32);

    private static List<Instruction> Decode(string bytes)
    {
        var data = Convert.FromHexString(bytes);
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(data), 0);
        var result = new List<Instruction>();
        while (decoder.IP < (ulong)data.Length)
            result.Add(decoder.Decode());
        return result;
    }
}
