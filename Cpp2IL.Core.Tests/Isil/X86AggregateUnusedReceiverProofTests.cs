using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X86AggregateUnusedReceiverProofTests
{
    [TestCase(0)]
    [TestCase(4)]
    public void ClosedBranchesDoNotInventAReceiverForTwoAggregateComponents(int component)
    {
        var body = Body(component);
        foreach (var read in body.Where(instruction => instruction.Mnemonic == Mnemonic.Movss))
            Assert.That(X64AggregateScalarOperandProof.TryFind(body, read.IP), Is.Not.Null);
        Assert.That(X86UnusedReceiverProof.IsUnused(body, Register.RCX), Is.False,
            "The existing entry-prefix proof must still stop at a branch.");
        Assert.That(X86UnusedReceiverProof.HasNoReceiverUseInClosedBody(body, Register.RCX), Is.True);
        Assert.That(X86UnusedReceiverProof.HasNoReceiverUseInClosedBody(body, Register.RDX), Is.False);
        Assert.That(X86UnusedReceiverProof.HasNoReceiverUseInClosedBody(body, Register.ECX), Is.False);
    }

    [TestCase("read")]
    [TestCase("copy")]
    [TestCase("memory-base")]
    [TestCase("memory-index")]
    [TestCase("implicit-read")]
    [TestCase("partial-write-read")]
    [TestCase("call")]
    [TestCase("indirect-call")]
    [TestCase("external-branch")]
    [TestCase("indirect-branch")]
    [TestCase("trap")]
    [TestCase("return-immediate")]
    public void ReceiverConsumersAndUnprovedControlFlowRemainUnresolved(string variant)
        => Assert.That(X86UnusedReceiverProof.HasNoReceiverUseInClosedBody(Body(0, variant), Register.RCX), Is.False);

    [Test]
    public void ExistingPrefixOverwriteProofRemainsIndependentOfClosedNoUseProof()
    {
        var body = Body(0, "full-overwrite");
        Assert.That(X86UnusedReceiverProof.IsUnused(body, Register.RCX), Is.True);
        Assert.That(X86UnusedReceiverProof.HasNoReceiverUseInClosedBody(body, Register.RCX), Is.False);
    }

    [TestCase("middle")]
    [TestCase("cycle")]
    [TestCase("external")]
    public void EveryBranchMustReachAnActualLaterInstruction(string target)
    {
        var body = Body(0);
        var index = Array.FindIndex(body, instruction => instruction.FlowControl == FlowControl.ConditionalBranch);
        body[index].NearBranch64 = target switch
        {
            "middle" => body[index].IP + 1,
            "cycle" => body[index].IP,
            _ => body[^1].NextIP
        };
        Assert.That(X86UnusedReceiverProof.HasNoReceiverUseInClosedBody(body, Register.RCX), Is.False);
    }

    [Test]
    public void MissingIntervalsUnreachableCodeAndFallthroughCannotCompleteTheRoot()
    {
        var body = Body(0);
        var gap = body.ToArray();
        gap[2].IP++;
        Assert.That(X86UnusedReceiverProof.HasNoReceiverUseInClosedBody(gap, Register.RCX), Is.False);
        Assert.That(X86UnusedReceiverProof.HasNoReceiverUseInClosedBody(body[..^1], Register.RCX), Is.False);
        var disconnected = X86Utils.Iterate(Convert.FromHexString("C390C3"), 0x1000, false).ToArray();
        Assert.That(X86UnusedReceiverProof.HasNoReceiverUseInClosedBody(disconnected, Register.RCX), Is.False);
    }

    private static Instruction[] Body(int component, string variant = "")
    {
        var assembler = new Assembler(64);
        var greater = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        assembler.mov(__qword_ptr[rsp + 0x10], rdx);
        assembler.movss(xmm0, __dword_ptr[rsp + 0x10 + component]);
        assembler.mov(__qword_ptr[rsp + 0x18], r8);
        assembler.movss(xmm1, __dword_ptr[rsp + 0x18 + component]);
        switch (variant)
        {
            case "read": assembler.mov(eax, ecx); break;
            case "copy": assembler.mov(r10, rcx); break;
            case "memory-base": assembler.mov(eax, __dword_ptr[rcx + 0x10]); break;
            case "memory-index": assembler.mov(eax, __dword_ptr[rdx + rcx]); break;
            case "implicit-read": assembler.loop(greater); break;
            case "partial-write-read": assembler.mov(cl, 7); assembler.mov(rax, rcx); break;
            case "full-overwrite": assembler.mov(ecx, 17); break;
            case "call": assembler.call(0x5000UL); break;
            case "indirect-call": assembler.call(rax); break;
            case "external-branch": assembler.jmp(0x5000UL); break;
            case "indirect-branch": assembler.jmp(rax); break;
            case "trap": assembler.int3(); break;
        }
        assembler.comiss(xmm0, xmm1);
        assembler.ja(greater);
        assembler.xor(eax, eax);
        assembler.comiss(xmm1, xmm0);
        assembler.seta(al);
        assembler.add(rsp, 0x28);
        if (variant == "return-immediate") assembler.ret(8);
        else assembler.ret();
        assembler.Label(ref greater);
        assembler.mov(eax, 1);
        assembler.add(rsp, 0x28);
        assembler.ret();
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x1000);
        return X86Utils.Iterate(stream.ToArray().AsSpan(), 0x1000, false).ToArray();
    }
}
