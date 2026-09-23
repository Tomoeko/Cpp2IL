using System;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86UnusedReceiverProofTests
{
    [TestCase("B82A000000C3")] // mov eax,42; ret: receiver is never read
    [TestCase("B91100000089C8C3")] // mov ecx,17; mov eax,ecx; ret: full overwrite first
    [TestCase("48B911000000000000004889C8C3")] // mov rcx,17; mov rax,rcx; ret
    [TestCase("B107B91100000089C8C3")] // partial write followed by a full overwrite
    public void UnusedEntryValueCanBeOmittedWithoutLosingLaterRegisterValues(string bytes)
        => Assert.That(Prove(bytes), Is.True);

    [TestCase("4889C8C3")] // mov rax,rcx; ret: direct receiver read
    [TestCase("8B4110C3")] // mov eax,[rcx+16]; ret: receiver in a memory address
    [TestCase("8B040AC3")] // mov eax,[rdx+rcx]; ret: receiver as memory index
    [TestCase("F3A4C3")] // rep movsb: implicit RCX read
    [TestCase("B1074889C8C3")] // mov cl,7; mov rax,rcx: upper receiver bits survive
    [TestCase("66B911004889C8C3")] // mov cx,17; mov rax,rcx: upper bits survive
    [TestCase("4889CA31C94889D0C3")] // copy receiver before overwriting it
    [TestCase("480F44CA4889C8C3")] // conditional RCX assignment cannot kill entry value
    [TestCase("E800000000B82A000000C3")] // opaque call can consume incoming receiver
    [TestCase("FFD0B82A000000C3")] // indirect call has the same ABI uncertainty
    [TestCase("EB00B82A000000C3")] // branch before the proposed no-use proof
    [TestCase("7400B82A000000C3")] // conditional branch before proof
    [TestCase("B8")] // truncated instruction
    [TestCase("90")] // no return or full overwrite establishes the proof
    [TestCase("")]
    public void ReadsPartialWritesAndAmbiguousControlFlowRetainTheWarning(string bytes)
        => Assert.That(Prove(bytes), Is.False);

    [Test]
    public void ReceiverRegisterComesFromTheActualParameterBinding()
    {
        var body = X86Utils.Disassemble(Convert.FromHexString("4889D0C3"), 0, false); // mov rax,rdx; ret
        Assert.That(X86UnusedReceiverProof.IsUnused(body, Register.RCX), Is.True);
        Assert.That(X86UnusedReceiverProof.IsUnused(body, Register.RDX), Is.False);
        Assert.That(X86UnusedReceiverProof.IsUnused(body, Register.EDX), Is.False);
    }

    [Test]
    public void DwordWriteRequiresAnExplicit64BitDecode()
    {
        var body = X86Utils.Disassemble(Convert.FromHexString("B91100000089C8C3"), 0, true);
        Assert.That(X86UnusedReceiverProof.IsUnused(body, Register.RCX), Is.False);
    }

    private static bool Prove(string bytes)
        => X86UnusedReceiverProof.IsUnused(X86Utils.Disassemble(Convert.FromHexString(bytes), 0, false), Register.RCX);
}
