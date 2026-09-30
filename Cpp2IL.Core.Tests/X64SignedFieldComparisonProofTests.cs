using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.Tests;

public class X64SignedFieldComparisonProofTests
{
    [TestCase(16)]
    [TestCase(88)]
    public void GuardedSignedBranchesRetainBothFieldReadRounds(int offset)
    {
        var proof = X64SignedFieldComparisonProof.TryProveShape(Body(offset));
        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.FieldOffset, Is.EqualTo(offset));
            Assert.That(proof.CapturesFields, Is.False);
            Assert.That(proof.NullCall.NearBranchTarget, Is.EqualTo(0x180020000UL));
        });
    }

    [TestCase("guard-order")]
    [TestCase("missing-first-guard")]
    [TestCase("guard-skips-second")]
    [TestCase("guard-to-result")]
    [TestCase("unsigned-less")]
    [TestCase("wrong-less-target")]
    [TestCase("less-zero")]
    [TestCase("second-origin")]
    [TestCase("first-origin")]
    [TestCase("first-offset")]
    [TestCase("second-round-offset")]
    [TestCase("last-offset")]
    [TestCase("unsigned-greater")]
    [TestCase("equal-greater")]
    [TestCase("uninitialized-result")]
    [TestCase("reused-second-value")]
    [TestCase("wide-field")]
    [TestCase("extra-write")]
    [TestCase("missing-trap")]
    public void ModifiedNullOrderReadIdentityOrSignedResultRejects(string variant)
        => Assert.That(X64SignedFieldComparisonProof.TryProveShape(Body(88, variant)), Is.Null);

    [Test]
    public void HeaderStorageUnalignedOffsetsAndDiscontinuousBodiesReject()
    {
        Assert.That(X64SignedFieldComparisonProof.TryProveShape(Body(8)), Is.Null);
        Assert.That(X64SignedFieldComparisonProof.TryProveShape(Body(17)), Is.Null);
        var body = Body(16);
        body[11].IP++;
        Assert.That(X64SignedFieldComparisonProof.TryProveShape(body), Is.Null);
    }

    [TestCase(16)]
    [TestCase(88)]
    public void CapturedOperandsRequireTheSameSignedBranchRelation(int offset)
    {
        var proof = X64SignedFieldComparisonProof.TryProveShape(CapturedBody(offset));
        Assert.That(proof, Is.Not.Null);
        Assert.That(proof!.CapturesFields, Is.True);
        Assert.That(proof.FieldOffset, Is.EqualTo(offset));
    }

    [TestCase("guard-order")]
    [TestCase("second-field-offset")]
    [TestCase("wrong-first-owner")]
    [TestCase("wide-first")]
    [TestCase("unsigned-less")]
    [TestCase("less-to-return")]
    [TestCase("second-compare-reversed")]
    [TestCase("unsigned-greater")]
    [TestCase("uninitialized-result")]
    [TestCase("less-zero")]
    public void CapturedModeDoesNotAdmitUnsignedOrDifferentOperandSemantics(string variant)
        => Assert.That(X64SignedFieldComparisonProof.TryProveShape(CapturedBody(88, variant)), Is.Null);

    private static NativeInstruction[] CapturedBody(int offset, string variant = "")
    {
        var assembler = new Assembler(64);
        var less = assembler.CreateLabel();
        var result = assembler.CreateLabel();
        var missing = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        assembler.test(variant == "guard-order" ? r8 : rdx, variant == "guard-order" ? r8 : rdx);
        assembler.je(missing);
        assembler.test(variant == "guard-order" ? rdx : r8, variant == "guard-order" ? rdx : r8);
        assembler.je(missing);
        if (variant == "wide-first") assembler.mov(rcx, __qword_ptr[rdx + offset]);
        else assembler.mov(ecx, __dword_ptr[(variant == "wrong-first-owner" ? r8 : rdx) + offset]);
        assembler.mov(edx, __dword_ptr[r8 + offset + (variant == "second-field-offset" ? 4 : 0)]);
        assembler.cmp(ecx, edx);
        if (variant == "unsigned-less") assembler.jb(less);
        else assembler.jl(variant == "less-to-return" ? result : less);
        if (variant == "uninitialized-result") assembler.nop();
        else assembler.xor(eax, eax);
        if (variant == "second-compare-reversed") assembler.cmp(edx, ecx);
        else assembler.cmp(ecx, edx);
        if (variant == "unsigned-greater") assembler.seta(al);
        else assembler.setg(al);
        assembler.Label(ref result);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref less);
        assembler.mov(eax, variant == "less-zero" ? 0U : uint.MaxValue);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref missing);
        assembler.call(0x180020000UL);
        assembler.int3();
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x180010000UL);
        return X86Utils.Disassemble(stream.ToArray(), 0x180010000UL, false).ToArray();
    }

    private static NativeInstruction[] Body(int offset, string variant = "")
    {
        var assembler = new Assembler(64);
        var greater = assembler.CreateLabel();
        var missing = assembler.CreateLabel();
        var result = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        if (variant == "missing-first-guard") assembler.nop();
        else assembler.test(variant == "guard-order" ? r8 : rdx, variant == "guard-order" ? r8 : rdx);
        assembler.je(variant == "guard-skips-second" ? greater : missing);
        assembler.test(variant == "guard-order" ? rdx : r8, variant == "guard-order" ? rdx : r8);
        assembler.je(variant == "guard-to-result" ? result : missing);
        if (variant == "wide-field") assembler.mov(rax, __qword_ptr[r8 + offset]);
        else assembler.mov(eax, __dword_ptr[(variant == "second-origin" ? rdx : r8) + offset]);
        assembler.cmp(__dword_ptr[(variant == "first-origin" ? r8 : rdx) + offset + (variant == "first-offset" ? 4 : 0)], eax);
        if (variant == "unsigned-less") assembler.jae(greater);
        else assembler.jge(variant == "wrong-less-target" ? result : greater);
        assembler.mov(eax, variant == "less-zero" ? 0U : uint.MaxValue);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref greater);
        if (variant == "reused-second-value") assembler.mov(ecx, eax);
        else assembler.mov(ecx, __dword_ptr[r8 + offset + (variant == "second-round-offset" ? 4 : 0)]);
        if (variant == "uninitialized-result") assembler.nop();
        else assembler.xor(eax, eax);
        assembler.cmp(__dword_ptr[rdx + offset + (variant == "last-offset" ? 4 : 0)], ecx);
        if (variant == "unsigned-greater") assembler.seta(al);
        else if (variant == "equal-greater") assembler.sete(al);
        else assembler.setg(al);
        assembler.Label(ref result);
        if (variant == "extra-write") assembler.inc(__dword_ptr[rdx + offset]);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref missing);
        assembler.call(0x180020000UL);
        if (variant != "missing-trap") assembler.int3();
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x180010000UL);
        return X86Utils.Disassemble(stream.ToArray(), 0x180010000UL, false).ToArray();
    }
}
