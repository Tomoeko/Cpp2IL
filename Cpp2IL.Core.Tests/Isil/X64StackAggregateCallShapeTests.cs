using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64StackAggregateCallShapeTests
{
    [Test]
    public void ACompleteEightPlusFourCopyKeepsOneWholeByValueArgument()
    {
        var body = Body();
        var copy = Find(body);
        Assert.That(copy, Is.Not.Null);
        Assert.That(copy!.StackOffset, Is.EqualTo(0x20));
        Assert.That(copy.ReplacedAddresses.Distinct().Count(), Is.EqualTo(4));
        Assert.That(X64StackAggregateCallProof.HasUnescapedParameterOrigin(body, copy, Register.RDX), Is.True);
        Assert.That(X64StackAggregateCallProof.HasUnescapedParameterOrigin(body, copy, Register.R8), Is.False);
    }

    [Test]
    public void AnIndependentlyCompleteReplacementEndsTheOldBuffersLifetime()
    {
        var body = Body("complete-reuse");
        Assert.That(Find(body), Is.Not.Null);
    }

    [TestCase("missing-high")]
    [TestCase("wrong-high-offset")]
    [TestCase("partial-high")]
    [TestCase("different-source")]
    [TestCase("overlapping-write")]
    [TestCase("packed-observer")]
    [TestCase("argument-pointer-copy")]
    [TestCase("old-read")]
    [TestCase("old-read-on-other-arm")]
    [TestCase("aliased-old-read")]
    [TestCase("partial-reuse-read")]
    [TestCase("address-escape")]
    [TestCase("prior-address-escape")]
    [TestCase("frame-alias")]
    [TestCase("callee-saved-scratch")]
    [TestCase("argument-old-read")]
    [TestCase("volatile-old-read")]
    [TestCase("volatile-skipped-overwrite")]
    [TestCase("packed-read-modify")]
    [TestCase("later-call-scratch")]
    [TestCase("partial-scratch-overwrite")]
    [TestCase("high-byte-overwrite")]
    [TestCase("partial-pointer-overwrite")]
    [TestCase("shadow-space")]
    public void IncompleteBytesObserversEscapesAndLiveOldSnapshotsReject(string defect)
    {
        Assert.That(Find(Body(defect)), Is.Null, defect);
    }

    [TestCase("source-write")]
    [TestCase("source-write-between-reads")]
    [TestCase("source-escape")]
    [TestCase("source-conflicting-join")]
    [TestCase("source-partial-alias")]
    [TestCase("source-address-alias")]
    public void AnOriginalByValueSignatureCannotReplaceNativeSourceProvenance(string defect)
    {
        var body = Body(defect);
        var copy = Find(body);
        Assert.That(copy, Is.Not.Null, "The byte-copy shape alone is intentionally insufficient.");
        Assert.That(X64StackAggregateCallProof.HasUnescapedParameterOrigin(body, copy!, Register.RDX), Is.False);
    }

    [Test]
    public void APredecessorPathCanSkipAnApparentMethodInfoZero()
    {
        var body = Body("skipped-zero");
        var copy = Find(body);
        Assert.That(copy, Is.Not.Null);
        Assert.That(X64StackAggregateCallProof.HasUnescapedParameterOrigin(body, copy!, Register.RDX), Is.True);
        Assert.That(X64StackAggregateCallProof.HasZeroRegisterArgument(body, copy!, Register.R8), Is.False);
        var original = Body();
        Assert.That(X64StackAggregateCallProof.HasZeroRegisterArgument(original, Find(original)!, Register.R8), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ASingleResultDefinesOnlyTheLowFourBytesOfPackedScratch(bool packedRead)
    {
        var body = Body(packedRead ? "single-result-packed-read" : "single-result-low-read");
        var call = body.Single(instruction => instruction.Code == Code.Call_rel32_64 && instruction.NearBranchTarget == 0x2000);
        var copy = X64StackAggregateCallProof.TryFindArgumentCopy(body, call.IP, Register.RDX, Register.XMM0, Register.XMM0, 4, 4);
        Assert.That(copy == null, Is.EqualTo(packedRead));
    }

    private static X64StackAggregateCallProof.ArgumentCopy? Find(Instruction[] body) =>
        X64StackAggregateCallProof.TryFindArgumentCopy(body, body.Single(instruction =>
            instruction.Code == Code.Call_rel32_64 && instruction.NearBranchTarget == 0x2000).IP, Register.RDX);

    private static Instruction[] Body(string defect = "")
    {
        var assembler = new Assembler(64);
        var other = assembler.CreateLabel(); var joined = assembler.CreateLabel();
        assembler.push(AssemblerRegisters.rbx);
        assembler.mov(AssemblerRegisters.rbx, AssemblerRegisters.rdx);
        assembler.sub(AssemblerRegisters.rsp, 0x40);
        if (defect == "skipped-zero")
        {
            assembler.test(AssemblerRegisters.rcx, AssemblerRegisters.rcx); assembler.je(joined);
            assembler.xor(AssemblerRegisters.r8d, AssemblerRegisters.r8d); assembler.Label(ref joined);
        }
        if (defect == "source-write") assembler.mov(AssemblerRegisters.__dword_ptr[AssemblerRegisters.rbx], 0);
        if (defect == "source-partial-alias")
        {
            assembler.mov(AssemblerRegisters.dl, 0); assembler.mov(AssemblerRegisters.r9, AssemblerRegisters.rdx);
        }
        if (defect == "source-address-alias") assembler.lea(AssemblerRegisters.r9, AssemblerRegisters.__qword_ptr[AssemblerRegisters.rbx]);
        if (defect == "source-escape") assembler.call(0x3000);
        if (defect == "source-conflicting-join")
        {
            assembler.test(AssemblerRegisters.rcx, AssemblerRegisters.rcx); assembler.je(other);
            assembler.mov(AssemblerRegisters.rbx, AssemblerRegisters.r8); assembler.jmp(joined);
            assembler.Label(ref other); assembler.nop(); assembler.Label(ref joined);
        }
        if (defect == "prior-address-escape") assembler.lea(AssemblerRegisters.r9, AssemblerRegisters.__qword_ptr[AssemblerRegisters.rsp + 0x20]);
        if (defect == "frame-alias") assembler.mov(AssemblerRegisters.r11, AssemblerRegisters.rsp);
        var lowScratch = defect == "callee-saved-scratch" ? AssemblerRegisters.xmm6 : AssemblerRegisters.xmm0;
        assembler.movsd(lowScratch, AssemblerRegisters.__qword_ptr[AssemblerRegisters.rbx]);
        if (defect == "source-write-between-reads") assembler.mov(AssemblerRegisters.__dword_ptr[AssemblerRegisters.rbx + 8], 0);
        assembler.mov(AssemblerRegisters.eax, AssemblerRegisters.__dword_ptr[
            (defect == "different-source" ? AssemblerRegisters.rsi : AssemblerRegisters.rbx) + 8]);
        var slot = defect == "shadow-space" ? 0x10 : 0x20;
        assembler.lea(AssemblerRegisters.rdx, AssemblerRegisters.__qword_ptr[AssemblerRegisters.rsp + slot]);
        if (defect == "argument-pointer-copy") assembler.mov(AssemblerRegisters.rsi, AssemblerRegisters.rdx);
        assembler.movsd(AssemblerRegisters.__qword_ptr[AssemblerRegisters.rsp + slot], lowScratch);
        if (defect != "missing-high")
        {
            if (defect == "partial-high") assembler.mov(AssemblerRegisters.__word_ptr[AssemblerRegisters.rsp + slot + 8], AssemblerRegisters.ax);
            else assembler.mov(AssemblerRegisters.__dword_ptr[AssemblerRegisters.rsp + slot + (defect == "wrong-high-offset" ? 4 : 8)], AssemblerRegisters.eax);
        }
        if (defect == "overlapping-write") assembler.mov(AssemblerRegisters.__dword_ptr[AssemblerRegisters.rsp + slot + 4], 0);
        if (defect == "packed-observer") assembler.comiss(AssemblerRegisters.xmm0, AssemblerRegisters.xmm1);
        if (defect == "packed-read-modify") assembler.addsd(AssemblerRegisters.xmm0, AssemblerRegisters.xmm1);
        if (defect == "partial-scratch-overwrite") assembler.mov(AssemblerRegisters.al, 0);
        if (defect == "high-byte-overwrite") assembler.mov(AssemblerRegisters.ah, 0);
        if (defect != "skipped-zero") assembler.xor(AssemblerRegisters.r8d, AssemblerRegisters.r8d);
        assembler.call(0x2000);
        if (defect == "single-result-low-read") assembler.movss(AssemblerRegisters.xmm1, AssemblerRegisters.xmm0);
        if (defect == "single-result-packed-read") assembler.movsd(AssemblerRegisters.xmm1, AssemblerRegisters.xmm0);
        if (defect is "partial-scratch-overwrite" or "high-byte-overwrite") assembler.mov(AssemblerRegisters.ecx, AssemblerRegisters.eax);
        if (defect == "partial-pointer-overwrite")
        {
            assembler.mov(AssemblerRegisters.dl, 0); assembler.mov(AssemblerRegisters.eax, AssemblerRegisters.__dword_ptr[AssemblerRegisters.rdx]);
        }
        if (defect == "callee-saved-scratch")
        {
            assembler.test(AssemblerRegisters.rcx, AssemblerRegisters.rcx); assembler.je(joined);
            assembler.xorps(AssemblerRegisters.xmm6, AssemblerRegisters.xmm6); assembler.Label(ref joined);
            assembler.comiss(AssemblerRegisters.xmm6, AssemblerRegisters.xmm1);
        }
        if (defect == "argument-old-read") assembler.mov(AssemblerRegisters.eax, AssemblerRegisters.__dword_ptr[AssemblerRegisters.rdx]);
        if (defect == "volatile-old-read") assembler.mov(AssemblerRegisters.ecx, AssemblerRegisters.eax);
        if (defect == "volatile-skipped-overwrite")
        {
            assembler.test(AssemblerRegisters.rcx, AssemblerRegisters.rcx); assembler.je(joined);
            assembler.xor(AssemblerRegisters.eax, AssemblerRegisters.eax); assembler.Label(ref joined);
            assembler.mov(AssemblerRegisters.ecx, AssemblerRegisters.eax);
        }
        if (defect == "later-call-scratch") assembler.call(0x4000);
        if (defect == "old-read-on-other-arm")
        {
            assembler.test(AssemblerRegisters.rcx, AssemblerRegisters.rcx); assembler.je(other);
            assembler.add(AssemblerRegisters.rsp, 0x40); assembler.pop(AssemblerRegisters.rbx); assembler.ret();
            assembler.Label(ref other); assembler.mov(AssemblerRegisters.eax, AssemblerRegisters.__dword_ptr[AssemblerRegisters.rsp + slot]);
        }
        if (defect is "complete-reuse" or "partial-reuse-read")
        {
            assembler.lea(AssemblerRegisters.r9, AssemblerRegisters.__qword_ptr[AssemblerRegisters.rsp + slot]);
            assembler.mov(AssemblerRegisters.__qword_ptr[AssemblerRegisters.rsp + slot], AssemblerRegisters.rcx);
            if (defect == "complete-reuse") assembler.mov(AssemblerRegisters.__dword_ptr[AssemblerRegisters.rsp + slot + 8], AssemblerRegisters.ecx);
            assembler.mov(AssemblerRegisters.eax, AssemblerRegisters.__dword_ptr[AssemblerRegisters.r9 + 8]);
        }
        if (defect == "old-read") assembler.mov(AssemblerRegisters.eax, AssemblerRegisters.__dword_ptr[AssemblerRegisters.rsp + slot]);
        if (defect == "aliased-old-read")
        {
            assembler.lea(AssemblerRegisters.r9, AssemblerRegisters.__qword_ptr[AssemblerRegisters.rsp + slot - 8]);
            assembler.mov(AssemblerRegisters.eax, AssemblerRegisters.__dword_ptr[AssemblerRegisters.r9 + 8]);
        }
        if (defect == "address-escape")
        {
            assembler.lea(AssemblerRegisters.r9, AssemblerRegisters.__qword_ptr[AssemblerRegisters.rsp + slot]); assembler.call(0x4000);
        }
        assembler.add(AssemblerRegisters.rsp, 0x40); assembler.pop(AssemblerRegisters.rbx); assembler.ret();
        using var bytes = new MemoryStream(); assembler.Assemble(new StreamCodeWriter(bytes), 0x1000);
        return X86Utils.Disassemble(bytes.ToArray(), 0x1000, false).ToArray();
    }
}
