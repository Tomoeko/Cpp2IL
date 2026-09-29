using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ReferenceFieldAddressStoreProofTests
{
    [TestCase("capture-first")]
    [TestCase("increment-first")]
    [TestCase("no-increment")]
    [TestCase("two-increments")]
    [TestCase("lea-address")]
    public void ACompleteTransferRetainsTheOrderedReadAndIncrementSites(string variant)
    {
        var body = Body(variant);
        var proof = X64ReferenceFieldAddressStoreProof.TryProveShape(body);
        Assert.That(proof, Is.Not.Null);
        Assert.That(proof!.SourceOffset, Is.EqualTo(0x20));
        Assert.That(proof.DestinationOffset, Is.EqualTo(0x10));
        Assert.That(proof.SourceAddress, Is.EqualTo(body.Single(instruction =>
            instruction.Code == Code.Mov_r64_rm64).IP));
        Assert.That(proof.Increments.Select(increment => increment.Address), Is.EqualTo(
            body.Where(instruction => instruction.Code == Code.Inc_rm32).Select(instruction => instruction.IP)));
        Assert.That(proof.StoreAddress, Is.EqualTo(body[^2].IP));
        Assert.That(proof.EndAddress, Is.EqualTo(body[^1].NextIP));
    }

    [TestCase("narrow-capture")]
    [TestCase("wrong-owner")]
    [TestCase("indexed-source")]
    [TestCase("narrow-increment")]
    [TestCase("clobbered-value")]
    [TestCase("second-capture")]
    [TestCase("prior-call")]
    [TestCase("wrong-address-register")]
    [TestCase("header-destination")]
    [TestCase("narrow-store")]
    [TestCase("store-offset")]
    [TestCase("wrong-stored-register")]
    [TestCase("indirect-tail")]
    public void ExtraEffectsAndChangedReferenceWidthsOrOriginsRemainUnproved(string variant)
        => Assert.That(X64ReferenceFieldAddressStoreProof.TryProveShape(Body(variant)), Is.Null);

    [Test]
    public void PrefixesInstructionGapsAndIncompleteTailsRemainUnproved()
    {
        var original = Body("capture-first");
        Assert.That(X64ReferenceFieldAddressStoreProof.TryProveShape(original[..^1]), Is.Null);
        var changed = original.ToArray();
        changed[0].SegmentPrefix = Register.GS;
        Assert.That(X64ReferenceFieldAddressStoreProof.TryProveShape(changed), Is.Null);
        changed = original.ToArray();
        changed[1].IP++;
        Assert.That(X64ReferenceFieldAddressStoreProof.TryProveShape(changed), Is.Null);
    }

    // All native encodings are assembled synthetic cases, independent of any player.
    private static Instruction[] Body(string variant)
    {
        var assembler = new Assembler(64);
        if (variant == "prior-call") assembler.call(0x4000UL);
        if (variant == "increment-first") assembler.inc(__dword_ptr[rcx + 0x28]);
        if (variant == "narrow-capture") assembler.mov(edx, __dword_ptr[rcx + 0x20]);
        else if (variant == "wrong-owner") assembler.mov(rdx, __qword_ptr[r8 + 0x20]);
        else if (variant == "indexed-source") assembler.mov(rdx, __qword_ptr[rcx + r8 * 8 + 0x20]);
        else assembler.mov(rdx, __qword_ptr[rcx + 0x20]);
        if (variant == "second-capture") assembler.mov(rdx, __qword_ptr[rcx + 0x18]);
        if (variant == "clobbered-value") assembler.xor(edx, edx);
        if (variant == "narrow-increment") assembler.inc(__word_ptr[rcx + 0x28]);
        else if (variant is not ("increment-first" or "no-increment")) assembler.inc(__dword_ptr[rcx + 0x28]);
        if (variant == "two-increments") assembler.inc(__dword_ptr[rcx + 0x28]);
        if (variant == "lea-address") assembler.lea(rcx, __qword_ptr[rcx + 0x10]);
        else if (variant == "wrong-address-register") assembler.add(r8, 0x10);
        else assembler.add(rcx, variant == "header-destination" ? 8 : 0x10);
        if (variant == "narrow-store") assembler.mov(__dword_ptr[rcx], edx);
        else if (variant == "store-offset") assembler.mov(__qword_ptr[rcx + 8], rdx);
        else if (variant == "wrong-stored-register") assembler.mov(__qword_ptr[rcx], r8);
        else assembler.mov(__qword_ptr[rcx], rdx);
        if (variant == "indirect-tail") assembler.jmp(rax);
        else assembler.jmp(0x5000UL);
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x1000);
        return X86Utils.Iterate(stream.ToArray().AsSpan(), 0x1000, false).ToArray();
    }
}
