using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64OwnerArrayBooleanEffectProofTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void BothEvidencedPrefixOrdersPreserveTheOwnerEffect(bool captureFirst)
    {
        var shape = Prove(Body(captureFirst));
        Assert.Multiple(() =>
        {
            Assert.That(shape?.ArrayOffset, Is.EqualTo(0x18));
            Assert.That(shape?.OwnerOffset, Is.EqualTo(0x14));
            Assert.That(shape?.ElementOffset, Is.EqualTo(0x11));
            Assert.That(shape?.OwnerValue, Is.False);
            Assert.That(shape?.ElementValue, Is.False);
            Assert.That(shape?.CapturesArrayBeforeOwnerEffect, Is.EqualTo(captureFirst));
        });
    }

    [TestCase("owner-width")]
    [TestCase("owner-receiver")]
    [TestCase("owner-literal")]
    [TestCase("array-receiver")]
    [TestCase("array-result")]
    [TestCase("array-null")]
    [TestCase("signed-bounds")]
    [TestCase("bounds-target")]
    [TestCase("index-width")]
    [TestCase("index-source")]
    [TestCase("element-stride")]
    [TestCase("element-header")]
    [TestCase("element-null")]
    [TestCase("element-literal")]
    [TestCase("element-width")]
    [TestCase("return")]
    [TestCase("null-tail")]
    [TestCase("bounds-tail")]
    [TestCase("trap")]
    [TestCase("stack")]
    [TestCase("gap")]
    [TestCase("prefix")]
    [TestCase("extra-effect")]
    [TestCase("missing-owner")]
    public void SimilarInstructionsDoNotAcquireTheOrderedEffectProof(string defect)
    {
        var body = Body(true);
        var index = defect switch
        {
            "array-receiver" or "array-result" => 1,
            "owner-width" or "owner-receiver" or "owner-literal" => 2,
            "array-null" => 4,
            "signed-bounds" or "bounds-target" => 6,
            "index-width" or "index-source" => 7,
            "element-stride" or "element-header" => 8,
            "element-null" => 10,
            "element-literal" or "element-width" => 11,
            "return" => 13,
            "null-tail" => 14,
            "bounds-tail" => 16,
            "trap" => 15,
            _ => 0,
        };
        var instruction = body[index];
        switch (defect)
        {
            case "owner-width": case "element-width": instruction.Code = Code.Mov_rm32_imm32; break;
            case "owner-receiver": instruction.MemoryBase = Register.R8; break;
            case "owner-literal": case "element-literal": instruction.Immediate8 = 2; break;
            case "array-receiver": instruction.MemoryBase = Register.RDX; break;
            case "array-result": instruction.Op0Register = Register.RAX; break;
            case "array-null": case "element-null": case "bounds-target": instruction.NearBranch64++; break;
            case "signed-bounds": instruction.Code = Code.Jge_rel8_64; break;
            case "index-width": instruction.Code = Code.Mov_r32_rm32; break;
            case "index-source": instruction.Op1Register = Register.R8D; break;
            case "element-stride": instruction.MemoryIndexScale = 4; break;
            case "element-header": instruction.MemoryDisplacement64++; break;
            case "return": instruction.Code = Code.Nopd; break;
            case "null-tail": case "bounds-tail": instruction.Code = Code.Jmp_rel32_64; break;
            case "trap": instruction.Code = Code.Nopd; break;
            case "stack": instruction.Immediate8to64 = 0x20; break;
            case "gap": instruction.IP++; break;
            case "prefix": instruction.HasLockPrefix = true; break;
            case "extra-effect": body = [..body, body[2]]; break;
            case "missing-owner": body = [body[0], body[1], ..body[3..]]; break;
        }
        if (defect is not ("extra-effect" or "missing-owner"))
            body[index] = instruction;
        Assert.That(Prove(body), Is.Null, defect);
    }

    [Test]
    public void ProfileWidthsAndIndependentBooleanLiteralsAreBound()
    {
        var body = Body(true);
        var owner = body[2];
        owner.Immediate8 = 1;
        body[2] = owner;
        var shape = Prove(body);
        Assert.Multiple(() =>
        {
            Assert.That(shape?.OwnerValue, Is.True);
            Assert.That(shape?.ElementValue, Is.False);
            Assert.That(X64ArrayElementBooleanStoreProof.TryProveOwnerEffectShape(body, 4, 0x18, 0x20), Is.Null);
            Assert.That(X64ArrayElementBooleanStoreProof.TryProveOwnerEffectShape(body, 8, 0x10, 0x20), Is.Null);
            Assert.That(X64ArrayElementBooleanStoreProof.TryProveOwnerEffectShape(body, 8, 0x18, 0x18), Is.Null);
        });
    }

    private static X64ArrayElementBooleanStoreProof.OwnerEffectShape? Prove(IReadOnlyList<Instruction> body) =>
        X64ArrayElementBooleanStoreProof.TryProveOwnerEffectShape(body, 8, 0x18, 0x20);

    private static Instruction[] Body(bool captureFirst)
    {
        // Synthetic offsets and helper displacements exercise the target ABI;
        // helper identity, complete executable bytes and metadata bind separately.
        var prefix = captureFirst ? "4C8B4118C6411400" : "C64114004C8B4118";
        var bytes = Convert.FromHexString("4883EC28" + prefix +
            "4D85C0741C413B5018731C4863C2498B4CC0204885C97409C64111004883C428C3E800010000CCE800020000");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), 0x1000);
        var instructions = new List<Instruction>();
        while (decoder.IP < 0x1000UL + (ulong)bytes.Length)
            instructions.Add(decoder.Decode());
        return instructions.ToArray();
    }
}
