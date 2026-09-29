using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ArrayLengthReadProofTests
{
    [TestCase("load")]
    [TestCase("compare")]
    [TestCase("prior-effect")]
    [TestCase("field-snapshot")]
    [TestCase("register-setup")]
    [TestCase("owner-read-setup")]
    public void Native32ReadKeepsItsGuardAndOperationIdentity(string variant)
    {
        var body = Body(variant);
        var read = body.Single(instruction => instruction.Op1Kind == OpKind.Memory &&
            instruction.MemoryDisplacement64 == 0x18);
        var proof = X64ArrayLengthReadProof.TryProveSite(body, read.IP);
        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.ReadAddress, Is.EqualTo(read.IP));
            Assert.That(proof.ArrayRegister, Is.EqualTo(read.MemoryBase));
            Assert.That(proof.ComparisonCapture, Is.EqualTo(variant == "compare"));
            Assert.That(proof.ResultRegister, Is.EqualTo(variant == "compare" ? Register.None : Register.RAX));
            Assert.That(proof.TestAddress, Is.EqualTo(body.Single(instruction => instruction.Mnemonic == Mnemonic.Test).IP));
            Assert.That(proof.BranchAddress, Is.EqualTo(body.Single(instruction => instruction.Mnemonic == Mnemonic.Je).IP));
            Assert.That(proof.NullCallAddress, Is.EqualTo(body[^1].IP));
            Assert.That(proof.NullTarget, Is.EqualTo(0x5000UL));
        });
    }

    [TestCase("long-length")]
    [TestCase("narrow-length")]
    [TestCase("wrong-offset")]
    [TestCase("indexed-length")]
    [TestCase("wrong-receiver")]
    [TestCase("narrow-test")]
    [TestCase("inverse-guard")]
    [TestCase("effect-after-guard")]
    [TestCase("clobbered-after-guard")]
    [TestCase("indirect-null-call")]
    [TestCase("read-bypass")]
    [TestCase("flags-bypass")]
    public void OtherWidthsGuardInputsAndBypassesRemainUnproved(string variant)
    {
        var body = Body(variant);
        var read = body.Single(instruction => instruction.Op1Kind == OpKind.Memory &&
            instruction.MemoryBase == Register.RDX);
        Assert.That(X64ArrayLengthReadProof.TryProveSite(body, read.IP), Is.Null);
    }

    [Test]
    public void PrefixesMissingNullTargetsAndInstructionGapsRemainUnproved()
    {
        var body = Body("load");
        var readIndex = Array.FindIndex(body, instruction => instruction.Code == Code.Mov_r32_rm32);
        var address = body[readIndex].IP;
        var changed = body.ToArray();
        changed[readIndex].SegmentPrefix = Register.GS;
        Assert.That(X64ArrayLengthReadProof.TryProveSite(changed, address), Is.Null);
        changed = body.ToArray();
        changed[readIndex - 1].NearBranch64++;
        Assert.That(X64ArrayLengthReadProof.TryProveSite(changed, address), Is.Null);
        changed = body.ToArray();
        changed[readIndex].IP++;
        Assert.That(X64ArrayLengthReadProof.TryProveSite(changed, address + 1), Is.Null);
        Assert.That(X64ArrayLengthReadProof.TryProveSite(body[..^1], address), Is.Null);
    }

    // Synthetic assembler cases do not contain bytes or identifiers from a player.
    private static Instruction[] Body(string variant)
    {
        var assembler = new Assembler(64);
        var nullArm = assembler.CreateLabel();
        var read = assembler.CreateLabel();
        var branch = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        if (variant == "prior-effect") assembler.inc(__dword_ptr[rcx + 0x28]);
        if (variant == "field-snapshot") assembler.mov(rdx, __qword_ptr[rcx + 0x20]);
        if (variant == "read-bypass") assembler.je(read);
        if (variant == "flags-bypass") assembler.je(branch);
        if (variant == "wrong-receiver") assembler.test(r8, r8);
        else if (variant == "narrow-test") assembler.test(edx, edx);
        else assembler.test(rdx, rdx);
        assembler.Label(ref branch);
        if (variant == "inverse-guard") assembler.jne(nullArm);
        else assembler.je(nullArm);
        if (variant == "effect-after-guard") assembler.inc(__dword_ptr[rcx + 0x28]);
        if (variant == "clobbered-after-guard") assembler.xor(edx, edx);
        if (variant == "register-setup")
        {
            assembler.xor(r9d, r9d);
            assembler.mov(eax, r8d);
        }
        if (variant == "owner-read-setup")
        {
            assembler.mov(r8d, __dword_ptr[rcx + 0x28]);
            assembler.xor(r9d, r9d);
        }
        assembler.Label(ref read);
        if (variant == "long-length") assembler.mov(rax, __qword_ptr[rdx + 0x18]);
        else if (variant == "narrow-length") assembler.mov(ax, __word_ptr[rdx + 0x18]);
        else if (variant == "wrong-offset") assembler.mov(eax, __dword_ptr[rdx + 0x10]);
        else if (variant == "indexed-length") assembler.mov(eax, __dword_ptr[rdx + r8 * 4 + 0x18]);
        else if (variant == "compare") assembler.cmp(eax, __dword_ptr[rdx + 0x18]);
        else assembler.mov(eax, __dword_ptr[rdx + 0x18]);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref nullArm);
        if (variant == "indirect-null-call") assembler.call(rax);
        else assembler.call(0x5000UL);
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x1000);
        return X86Utils.Iterate(stream.ToArray().AsSpan(), 0x1000, false).ToArray();
    }
}
