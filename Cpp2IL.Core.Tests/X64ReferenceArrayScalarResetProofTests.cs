using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.Tests;

public class X64ReferenceArrayScalarResetProofTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void LengthLoopRetainsMarkerStoreSeparateArrayReadsAndAllExceptionalExits(bool padding)
    {
        var proof = X64ReferenceArrayScalarResetProof.TryProveShape(LengthBody(padding));
        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.Mode, Is.EqualTo(X64ReferenceArrayScalarResetProof.LoopMode.ReloadedLength));
            Assert.That(proof.ArrayOffset, Is.EqualTo(24));
            Assert.That(proof.MarkerOffset, Is.EqualTo(16));
            Assert.That(proof.ElementOffset, Is.EqualTo(16));
            Assert.That(proof.NullCall.NearBranchTarget, Is.EqualTo(0x4000));
            Assert.That(proof.BoundsCall.NearBranchTarget, Is.EqualTo(0x5000));
        });
    }

    [TestCase(false, false, 1)]
    [TestCase(false, true, 7)]
    [TestCase(true, false, 31)]
    [TestCase(true, true, 127)]
    public void FixedLoopDerivesCountAndBothIndexRegisterAllocations(bool edxIndex, bool repeatNull, int count)
    {
        var proof = X64ReferenceArrayScalarResetProof.TryProveShape(FixedBody(edxIndex, repeatNull, count));
        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.Mode, Is.EqualTo(X64ReferenceArrayScalarResetProof.LoopMode.FixedCount));
            Assert.That(proof.Count, Is.EqualTo(count));
            Assert.That(proof.RepeatsArrayNullGuard, Is.EqualTo(repeatNull));
            Assert.That(proof.ArrayOffset, Is.EqualTo(24));
            Assert.That(proof.ElementOffset, Is.EqualTo(16));
        });
    }

    [TestCase("marker-value")]
    [TestCase("marker-width")]
    [TestCase("owner-header")]
    [TestCase("first-null-return")]
    [TestCase("unsigned-termination")]
    [TestCase("different-inner-array")]
    [TestCase("signed-bounds")]
    [TestCase("bounds-to-null")]
    [TestCase("unsigned-index-extension")]
    [TestCase("element-stride")]
    [TestCase("element-null-return")]
    [TestCase("decrement")]
    [TestCase("store-one")]
    [TestCase("wide-store")]
    [TestCase("different-reload-array")]
    [TestCase("wrong-counter-copy")]
    [TestCase("reload-null-return")]
    [TestCase("skip-length-test")]
    [TestCase("missing-trap")]
    [TestCase("extra-effect")]
    public void LengthLoopRejectsChangedEffectsReloadsBoundsAndFaultOrder(string variant) =>
        Assert.That(X64ReferenceArrayScalarResetProof.TryProveShape(LengthBody(true, variant)), Is.Null);

    [TestCase("owner-header")]
    [TestCase("nonzero-clear")]
    [TestCase("wrong-index-copy")]
    [TestCase("array-null-return")]
    [TestCase("signed-bounds")]
    [TestCase("bounds-to-null")]
    [TestCase("unsigned-index-extension")]
    [TestCase("element-stride")]
    [TestCase("element-null-return")]
    [TestCase("decrement")]
    [TestCase("store-owner")]
    [TestCase("store-index")]
    [TestCase("wide-store")]
    [TestCase("unsigned-termination")]
    [TestCase("skip-bounds")]
    [TestCase("missing-trap")]
    [TestCase("extra-effect")]
    public void FixedLoopRejectsChangedPartialWritesAndExceptionalPaths(string variant) =>
        Assert.That(X64ReferenceArrayScalarResetProof.TryProveShape(FixedBody(true, true, 7, variant)), Is.Null);

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(128)]
    public void UnprovedLoopCountsAreRejected(int count) =>
        Assert.That(X64ReferenceArrayScalarResetProof.TryProveShape(FixedBody(true, true, count)), Is.Null);

    [TestCase(false)]
    [TestCase(true)]
    public void NativeBodyMustBeContiguousAndEveryInstructionAccountedFor(bool length)
    {
        var body = length ? LengthBody(false) : FixedBody(true, true, 7);
        var changed = body[9];
        changed.IP++;
        body[9] = changed;
        Assert.That(X64ReferenceArrayScalarResetProof.TryProveShape(body), Is.Null);
        body = length ? LengthBody(false) : FixedBody(true, true, 7);
        body[^1] = Instruction.Create(Code.Retnq);
        Assert.That(X64ReferenceArrayScalarResetProof.TryProveShape(body), Is.Null);
    }

    private static NativeInstruction[] LengthBody(bool padding, string variant = "")
    {
        var a = new Assembler(64);
        var loop = a.CreateLabel();
        var store = a.CreateLabel();
        var done = a.CreateLabel();
        var nullExit = a.CreateLabel();
        var boundsExit = a.CreateLabel();
        a.sub(rsp, 0x28);
        if (variant == "marker-value") a.mov(edx, 1);
        else a.xor(edx, edx);
        a.mov(r9, rcx);
        if (variant == "marker-width") a.mov(__qword_ptr[rcx + 16], rdx);
        else a.mov(__dword_ptr[rcx + (variant == "owner-header" ? 8 : 16)], edx);
        a.mov(ecx, edx);
        a.mov(rax, __qword_ptr[r9 + 24]);
        a.test(rax, rax);
        a.je(variant == "first-null-return" ? done : nullExit);
        if (padding) a.nop();
        a.Label(ref loop);
        a.cmp(ecx, __dword_ptr[rax + 24]);
        if (variant == "unsigned-termination") a.jae(done);
        else a.jge(done);
        a.mov(rcx, __qword_ptr[r9 + (variant == "different-inner-array" ? 40 : 24)]);
        a.test(rcx, rcx);
        a.je(nullExit);
        a.cmp(edx, __dword_ptr[rcx + 24]);
        if (variant == "signed-bounds") a.jge(boundsExit);
        else a.jae(variant == "bounds-to-null" ? nullExit : boundsExit);
        if (variant == "unsigned-index-extension") a.mov(eax, edx);
        else a.movsxd(rax, edx);
        a.mov(r8, __qword_ptr[rcx + rax * (variant == "element-stride" ? 4 : 8) + 32]);
        a.test(r8, r8);
        a.je(variant == "element-null-return" ? done : nullExit);
        if (variant == "decrement") a.dec(edx);
        else a.inc(edx);
        a.Label(ref store);
        if (variant == "wide-store") a.mov(__dword_ptr[r8 + 16], 0);
        else a.mov(__byte_ptr[r8 + 16], variant == "store-one" ? 1 : 0);
        if (variant == "extra-effect") a.inc(__dword_ptr[r9 + 48]);
        a.mov(rax, __qword_ptr[r9 + (variant == "different-reload-array" ? 40 : 24)]);
        if (variant == "wrong-counter-copy") a.mov(ecx, r9d);
        else a.mov(ecx, edx);
        a.test(rax, rax);
        a.je(variant == "reload-null-return" ? done : nullExit);
        a.jmp(variant == "skip-length-test" ? store : loop);
        a.Label(ref done);
        a.add(rsp, 0x28);
        a.ret();
        a.Label(ref nullExit);
        a.call(0x4000UL);
        if (variant != "missing-trap") a.int3();
        a.Label(ref boundsExit);
        a.call(0x5000UL);
        a.int3();
        return Decode(a);
    }

    private static NativeInstruction[] FixedBody(bool edxIndex, bool repeatNull, int count, string variant = "")
    {
        var a = new Assembler(64);
        var loop = a.CreateLabel();
        var bounds = a.CreateLabel();
        var store = a.CreateLabel();
        var done = a.CreateLabel();
        var nullExit = a.CreateLabel();
        var boundsExit = a.CreateLabel();
        a.sub(rsp, 0x28);
        a.mov(r8, __qword_ptr[rcx + (variant == "owner-header" ? 8 : 24)]);
        if (variant == "nonzero-clear") a.mov(r9d, 1);
        else a.xor(r9d, r9d);
        var index = edxIndex ? edx : ecx;
        var element = edxIndex ? rcx : rdx;
        a.mov(index, variant == "wrong-index-copy" ? eax : r9d);
        a.Label(ref loop);
        a.test(r8, r8);
        a.je(variant == "array-null-return" ? done : nullExit);
        a.Label(ref bounds);
        a.cmp(index, __dword_ptr[r8 + 24]);
        if (variant == "signed-bounds") a.jge(boundsExit);
        else a.jae(variant == "bounds-to-null" ? nullExit : boundsExit);
        if (variant == "unsigned-index-extension") a.mov(eax, index);
        else a.movsxd(rax, index);
        a.mov(element, __qword_ptr[r8 + rax * (variant == "element-stride" ? 4 : 8) + 32]);
        a.test(element, element);
        a.je(variant == "element-null-return" ? done : nullExit);
        if (variant == "decrement") a.dec(index);
        else a.inc(index);
        a.Label(ref store);
        if (variant == "wide-store") a.mov(__qword_ptr[element + 16], r9);
        else a.mov(__dword_ptr[(variant == "store-owner" ? r8 : element) + 16],
            variant == "store-index" ? index : r9d);
        if (variant == "extra-effect") a.inc(__dword_ptr[r8 + 48]);
        a.cmp(index, count);
        if (variant == "unsigned-termination") a.jb(loop);
        else a.jl(variant == "skip-bounds" ? store : repeatNull ? loop : bounds);
        a.add(rsp, 0x28);
        a.Label(ref done);
        a.ret();
        a.Label(ref nullExit);
        a.call(0x4000UL);
        if (variant != "missing-trap") a.int3();
        a.Label(ref boundsExit);
        a.call(0x5000UL);
        a.int3();
        return Decode(a);
    }

    private static NativeInstruction[] Decode(Assembler assembler)
    {
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x1000);
        return X86Utils.Iterate(stream.ToArray().AsSpan(), 0x1000, false).ToArray();
    }
}
