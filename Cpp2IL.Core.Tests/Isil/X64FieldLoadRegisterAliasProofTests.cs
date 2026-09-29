using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64FieldLoadRegisterAliasProofTests
{
    [TestCase("direct")]
    [TestCase("preserved-call")]
    [TestCase("diamond")]
    [TestCase("owner-store")]
    public void AFieldSnapshotSurvivesOnlyItsOwnFullWidthCopies(string variant)
    {
        var body = Body(variant);
        var load = body.Single(instruction => instruction.Code == Code.Mov_r64_rm64 &&
            instruction.Op1Kind == OpKind.Memory);
        Assert.That(X64NativeRegisterAliasProof.IsAlias(body, load.IP,
            load.MemoryBase, Register.RCX), Is.True);
        Assert.That(X64NativeRegisterAliasProof.IsAliasFromFieldLoad(body,
            body[^2].IP, variant == "direct" ? Register.RAX : Register.RBX, load.IP), Is.True);
    }

    [TestCase("volatile-call")]
    [TestCase("partial-write")]
    [TestCase("bypassed-load")]
    [TestCase("changed-path")]
    [TestCase("reread")]
    [TestCase("dword-load")]
    [TestCase("stack-load")]
    [TestCase("indexed-load")]
    [TestCase("external-branch")]
    public void BypassesRereadsPartialWritesAndNonFieldLoadsAreRejected(string variant)
    {
        var body = Body(variant);
        var load = body.First(instruction => instruction.Op0Kind == OpKind.Register &&
            instruction.Op1Kind == OpKind.Memory);
        Assert.That(X64NativeRegisterAliasProof.IsAliasFromFieldLoad(body,
            body[^2].IP, variant == "volatile-call" ? Register.RAX : Register.RBX, load.IP), Is.False);
    }

    [Test]
    public void AChangedOwnerPathCannotAuthenticateTheSourceField()
    {
        var body = Body("changed-owner");
        var load = body.Single(instruction => instruction.Code == Code.Mov_r64_rm64 &&
            instruction.Op1Kind == OpKind.Memory);
        Assert.That(X64NativeRegisterAliasProof.IsAlias(body, load.IP,
            load.MemoryBase, Register.RCX), Is.False);
        Assert.That(X64NativeRegisterAliasProof.IsAliasFromFieldLoad(body,
            body[^2].IP, Register.RBX, load.IP), Is.True,
            "Snapshot identity and source-object identity are separate obligations.");
    }

    [Test]
    public void TheExactLoadSiteAndNativePrefixRemainRequired()
    {
        var body = Body("direct");
        Assert.That(X64NativeRegisterAliasProof.IsAliasFromFieldLoad(body,
            body[^2].IP, Register.RAX, body[0].IP + 1), Is.False);
        var changed = body[0];
        changed.SegmentPrefix = Register.GS;
        body[0] = changed;
        Assert.That(X64NativeRegisterAliasProof.IsAliasFromFieldLoad(body,
            body[^2].IP, Register.RAX, body[0].IP), Is.False);
    }

    // Synthetic sequences assembled here, independent of any application binary.
    private static Instruction[] Body(string variant)
    {
        var assembler = new Assembler(64);
        var right = assembler.CreateLabel();
        var joined = assembler.CreateLabel();
        if (variant == "bypassed-load")
        {
            assembler.test(rdx, rdx);
            assembler.je(joined);
        }
        if (variant == "changed-owner")
        {
            assembler.test(rdx, rdx);
            assembler.je(right);
            assembler.mov(rdi, rcx);
            assembler.jmp(joined);
            assembler.Label(ref right);
            assembler.mov(rdi, rdx);
            assembler.Label(ref joined);
            assembler.mov(rax, __qword_ptr[rdi + 0x30]);
        }
        else if (variant == "dword-load") assembler.mov(eax, __dword_ptr[rcx + 0x30]);
        else if (variant == "stack-load") assembler.mov(rax, __qword_ptr[rsp + 0x30]);
        else if (variant == "indexed-load") assembler.mov(rax, __qword_ptr[rcx + rdx * 8 + 0x30]);
        else assembler.mov(rax, __qword_ptr[rcx + 0x30]);
        if (variant == "bypassed-load") assembler.Label(ref joined);
        if (variant is "diamond" or "changed-path")
        {
            assembler.test(rdx, rdx);
            assembler.je(right);
            assembler.mov(rbx, rax);
            assembler.jmp(joined);
            assembler.Label(ref right);
            if (variant == "changed-path") assembler.mov(rbx, rdx);
            else assembler.mov(rbx, rax);
            assembler.Label(ref joined);
        }
        else if (variant != "direct" && variant != "volatile-call") assembler.mov(rbx, rax);
        if (variant is "preserved-call" or "volatile-call") assembler.call(0x5000UL);
        if (variant == "partial-write") assembler.mov(bl, 1);
        if (variant == "reread") assembler.mov(rbx, __qword_ptr[rcx + 0x30]);
        if (variant == "owner-store") assembler.mov(__qword_ptr[rcx + 0x30], rdx);
        if (variant == "external-branch") assembler.jmp(0x9000UL);
        if (variant is "direct" or "volatile-call") assembler.mov(__byte_ptr[rax + 0x18], 1);
        else assembler.mov(__byte_ptr[rbx + 0x18], 1);
        assembler.ret();
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x1000);
        return X86Utils.Iterate(stream.ToArray().AsSpan(), 0x1000, false).ToArray();
    }
}
