using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.Tests;

public class X64ReferenceArraySearchProofTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void CompleteFirstMatchBindsBothReadsAndResultConversion(bool boolean, bool padding)
    {
        var proof = X64ReferenceArraySearchProof.TryProveShape(Body(boolean, padding));
        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.ArrayOffset, Is.EqualTo(0x18));
            Assert.That(proof.KeyOffset, Is.EqualTo(0x28));
            Assert.That(proof.ReturnsBoolean, Is.EqualTo(boolean));
            Assert.That(proof.BoundsCall.NearBranchTarget, Is.EqualTo(0x180020000UL));
            Assert.That(proof.NullCall!.Value.NearBranchTarget, Is.EqualTo(0x180030000UL));
        });
    }

    [TestCase("skip-result-conversion")]
    [TestCase("unsigned-loop")]
    [TestCase("signed-bounds")]
    [TestCase("bounds-to-null")]
    [TestCase("null-to-absence")]
    [TestCase("wrong-key-origin")]
    [TestCase("wide-key")]
    [TestCase("cached-element")]
    [TestCase("different-second-index")]
    [TestCase("wide-loop-length")]
    [TestCase("wrong-element-stride")]
    [TestCase("decrement")]
    [TestCase("backedge-skips-length")]
    [TestCase("skip-key-on-null")]
    [TestCase("absence-zero")]
    [TestCase("wrong-boolean-sign-bit")]
    [TestCase("extra-effect")]
    [TestCase("missing-trap")]
    public void ChangedNativeSearchSemanticsAreRejected(string variant)
        => Assert.That(X64ReferenceArraySearchProof.TryProveShape(Body(true, true, variant)), Is.Null);

    [Test]
    public void UnboundOffsetsAndDiscontinuousInstructionsReject()
    {
        Assert.That(X64ReferenceArraySearchProof.TryProveShape(Body(false, false, "header-array")), Is.Null);
        Assert.That(X64ReferenceArraySearchProof.TryProveShape(Body(false, false, "header-key")), Is.Null);
        var body = Body(false, true);
        body[10].IP++;
        Assert.That(X64ReferenceArraySearchProof.TryProveShape(body), Is.Null);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void CapturedLengthAndElementKeepTheirCompleteFirstMatchRelation(bool boolean, bool padding)
    {
        var proof = X64ReferenceArraySearchProof.TryProveShape(CapturedBody(boolean, padding));
        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.ReturnsBoolean, Is.EqualTo(boolean));
            Assert.That(proof.CapturesElement, Is.True);
            Assert.That(proof.NullCall, Is.Null);
            Assert.That(proof.BoundsCall.NearBranchTarget, Is.EqualTo(0x180020000UL));
        });
    }

    [TestCase("skip-result-conversion")]
    [TestCase("skip-captured-index")]
    [TestCase("null-skips-conversion")]
    [TestCase("unsigned-loop")]
    [TestCase("signed-bounds")]
    [TestCase("different-bound")]
    [TestCase("wrong-key-origin")]
    [TestCase("wide-length")]
    [TestCase("wrong-element-stride")]
    [TestCase("wrong-index-origin")]
    [TestCase("null-to-result")]
    [TestCase("wide-key")]
    [TestCase("decrement")]
    [TestCase("backedge-skips-length")]
    [TestCase("wrong-boolean-sign-bit")]
    [TestCase("extra-effect")]
    [TestCase("missing-trap")]
    public void CapturedSearchRejectsChangedBoundsIdentityAndConversion(string variant)
        => Assert.That(X64ReferenceArraySearchProof.TryProveShape(CapturedBody(true, true, variant)), Is.Null);

    [TestCase(false, false, "owner-40")]
    [TestCase(false, true, "owner-40")]
    [TestCase(true, false, "owner-40")]
    [TestCase(true, true, "owner-40")]
    [TestCase(false, false, "owner-64")]
    [TestCase(false, true, "owner-64")]
    [TestCase(true, false, "owner-64")]
    [TestCase(true, true, "owner-64")]
    [TestCase(false, false, "owner-4096")]
    [TestCase(false, true, "owner-4096")]
    [TestCase(true, false, "owner-4096")]
    [TestCase(true, true, "owner-4096")]
    public void UnguardedOwnerOffsetsRequireEstablishedManagedNullFaults(bool boolean, bool captured, string variant)
        => Assert.That(X64ReferenceArraySearchProof.TryProveShape(captured
            ? CapturedBody(boolean, false, variant) : Body(boolean, false, variant)), Is.Null);

    private static NativeInstruction[] CapturedBody(bool boolean, bool padding, string variant = "")
    {
        var assembler = new Assembler(64);
        var loop = assembler.CreateLabel();
        var access = assembler.CreateLabel();
        var increment = assembler.CreateLabel();
        var result = assembler.CreateLabel();
        var missing = assembler.CreateLabel();
        var epilogue = assembler.CreateLabel();
        var bounds = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        assembler.mov(r8, __qword_ptr[rcx + OwnerOffset(variant)]);
        assembler.mov(boolean ? r11d : r10d, variant == "wrong-key-origin" ? ecx : edx);
        if (boolean) assembler.mov(r9d, uint.MaxValue);
        assembler.test(r8, r8);
        assembler.je(variant == "null-skips-conversion" ? epilogue : missing);
        if (variant == "wide-length") assembler.mov(r10, __qword_ptr[r8 + 0x18]);
        else assembler.mov(boolean ? r10d : r9d, __dword_ptr[r8 + 0x18]);
        assembler.xor(eax, eax);
        if (padding) assembler.nop();
        assembler.Label(ref loop);
        assembler.mov(ecx, eax);
        assembler.cmp(eax, boolean ? r10d : r9d);
        if (variant == "unsigned-loop") assembler.jae(missing);
        else assembler.jge(missing);
        assembler.Label(ref access);
        assembler.cmp(eax, variant == "different-bound" ? r9d : boolean ? r10d : r9d);
        if (variant == "signed-bounds") assembler.jge(bounds);
        else assembler.jae(bounds);
        assembler.movsxd(rcx, variant == "wrong-index-origin" ? edx : eax);
        assembler.mov(rdx, __qword_ptr[r8 + rcx * (variant == "wrong-element-stride" ? 4 : 8) + 0x20]);
        assembler.test(rdx, rdx);
        assembler.je(variant == "null-to-result" ? result : increment);
        if (variant == "wide-key") assembler.cmp(__qword_ptr[rdx + 0x28], r11);
        else assembler.cmp(__dword_ptr[rdx + 0x28], boolean ? r11d : r10d);
        assembler.je(variant == "skip-result-conversion" ? epilogue : variant == "skip-captured-index" ? missing :
            boolean ? result : epilogue);
        assembler.Label(ref increment);
        if (variant == "decrement") assembler.dec(eax);
        else assembler.inc(eax);
        if (variant == "extra-effect") assembler.inc(__dword_ptr[r8 + 0x10]);
        assembler.jmp(variant == "backedge-skips-length" ? access : loop);
        if (boolean)
        {
            assembler.Label(ref result);
            assembler.mov(r9d, eax);
            assembler.Label(ref missing);
            assembler.shr(r9d, (byte)(variant == "wrong-boolean-sign-bit" ? 30 : 31));
            assembler.xor(r9b, 1);
            assembler.movzx(eax, r9b);
        }
        else
        {
            assembler.Label(ref missing);
            assembler.mov(eax, uint.MaxValue);
        }
        assembler.Label(ref epilogue);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref bounds);
        assembler.call(0x180020000);
        if (variant == "missing-trap") assembler.nop();
        else assembler.int3();
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x180010000);
        return X86Utils.Disassemble(stream.ToArray(), 0x180010000, false).ToArray();
    }

    private static NativeInstruction[] Body(bool boolean, bool padding, string variant = "")
    {
        var assembler = new Assembler(64);
        var loop = assembler.CreateLabel();
        var access = assembler.CreateLabel();
        var increment = assembler.CreateLabel();
        var missing = assembler.CreateLabel();
        var result = assembler.CreateLabel();
        var epilogue = assembler.CreateLabel();
        var bounds = assembler.CreateLabel();
        var nullExit = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        assembler.mov(r8, __qword_ptr[rcx + (variant == "header-array" ? 8 : OwnerOffset(variant))]);
        assembler.mov(r9d, variant == "wrong-key-origin" ? ecx : edx);
        assembler.xor(eax, eax);
        assembler.test(r8, r8);
        assembler.je(missing);
        if (padding) assembler.nop();
        assembler.Label(ref loop);
        if (variant == "wide-loop-length") assembler.cmp(rax, __qword_ptr[r8 + 0x18]);
        else assembler.cmp(eax, __dword_ptr[r8 + 0x18]);
        if (variant == "unsigned-loop") assembler.jae(missing);
        else assembler.jge(missing);
        assembler.Label(ref access);
        assembler.test(r8, r8);
        assembler.je(variant == "null-to-absence" ? missing : nullExit);
        assembler.cmp(eax, __dword_ptr[r8 + 0x18]);
        if (variant == "signed-bounds") assembler.jge(bounds);
        else assembler.jae(variant == "bounds-to-null" ? nullExit : bounds);
        assembler.movsxd(rcx, eax);
        assembler.cmp(__qword_ptr[r8 + rcx * (variant == "wrong-element-stride" ? 4 : 8) + 0x20], 0);
        assembler.je(variant == "skip-key-on-null" ? result : increment);
        if (variant == "different-second-index") assembler.movsxd(rcx, r9d);
        else assembler.movsxd(rcx, eax);
        if (variant == "cached-element") assembler.mov(rdx, r8);
        else assembler.mov(rdx, __qword_ptr[r8 + rcx * 8 + 0x20]);
        if (variant == "wide-key") assembler.cmp(__qword_ptr[rdx + 0x28], r9);
        else assembler.cmp(__dword_ptr[rdx + (variant == "header-key" ? 8 : 0x28)], r9d);
        assembler.je(variant == "skip-result-conversion" ? epilogue : result);
        assembler.Label(ref increment);
        if (variant == "decrement") assembler.dec(eax);
        else assembler.inc(eax);
        if (variant == "extra-effect") assembler.inc(__dword_ptr[r8 + 0x10]);
        assembler.jmp(variant == "backedge-skips-length" ? access : loop);
        assembler.Label(ref missing);
        assembler.mov(eax, variant == "absence-zero" ? 0 : uint.MaxValue);
        if (boolean)
        {
            assembler.Label(ref result);
            assembler.shr(eax, (byte)(variant == "wrong-boolean-sign-bit" ? 30 : 31));
            assembler.xor(al, 1);
        }
        if (boolean) assembler.Label(ref epilogue);
        else assembler.Label(ref result);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref bounds);
        assembler.call(0x180020000);
        if (variant == "missing-trap") assembler.nop();
        else assembler.int3();
        assembler.Label(ref nullExit);
        assembler.call(0x180030000);
        assembler.int3();
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x180010000);
        return X86Utils.Disassemble(stream.ToArray(), 0x180010000, false).ToArray();
    }

    private static int OwnerOffset(string variant) => variant switch
    {
        "owner-40" => 40,
        "owner-64" => 64,
        "owner-4096" => 4096,
        _ => 24
    };
}
