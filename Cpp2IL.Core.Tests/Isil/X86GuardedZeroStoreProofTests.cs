using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86GuardedZeroStoreProofTests
{
    [Test]
    public void DirectBooleanAndIntegerAndNestedIntegerShapesKeepTheirWidthsAndOffsets()
    {
        var direct = X86GuardedZeroStoreProof.TryProveShape(Body(false));
        var directInteger = X86GuardedZeroStoreProof.TryProveShape(Body(false, true));
        var nested = X86GuardedZeroStoreProof.TryProveShape(Body(true));
        Assert.Multiple(() =>
        {
            Assert.That(direct, Is.Not.Null);
            Assert.That(direct!.ReceiverOffset, Is.Null);
            Assert.That(direct.StoreOffset, Is.EqualTo(0x10));
            Assert.That(direct.StoreWidth, Is.EqualTo(1));
            Assert.That(directInteger, Is.Not.Null);
            Assert.That(directInteger!.ReceiverOffset, Is.Null);
            Assert.That(directInteger.StoreOffset, Is.EqualTo(0x10));
            Assert.That(directInteger.StoreWidth, Is.EqualTo(4));
            Assert.That(nested, Is.Not.Null);
            Assert.That(nested!.ReceiverOffset, Is.EqualTo(0x10));
            Assert.That(nested.StoreOffset, Is.EqualTo(0x10));
            Assert.That(nested.StoreWidth, Is.EqualTo(4));
        });
    }

    [Test]
    public void DirectIntegerZeroStoreRequiresZeroAndTheGuardedReceiver()
    {
        var nonzero = Body(false, true);
        var changedValue = nonzero[3];
        changedValue.Immediate32 = 1;
        nonzero[3] = changedValue;

        var wrongReceiver = Body(false, true);
        var changedReceiver = wrongReceiver[3];
        changedReceiver.MemoryBase = Register.RDX;
        wrongReceiver[3] = changedReceiver;

        Assert.Multiple(() =>
        {
            Assert.That(X86GuardedZeroStoreProof.TryProveShape(nonzero), Is.Null);
            Assert.That(X86GuardedZeroStoreProof.TryProveShape(wrongReceiver), Is.Null);
        });
    }

    [TestCase(false, "nonzero")]
    [TestCase(false, "wide-store")]
    [TestCase(false, "wrong-receiver")]
    [TestCase(false, "wrong-branch")]
    [TestCase(false, "wrong-stack")]
    [TestCase(false, "call-falls-through")]
    [TestCase(false, "locked-store")]
    [TestCase(true, "nonzero")]
    [TestCase(true, "narrow-store")]
    [TestCase(true, "wrong-receiver")]
    [TestCase(true, "wrong-parent-load")]
    [TestCase(true, "wrong-branch")]
    [TestCase(true, "wrong-stack")]
    [TestCase(true, "call-falls-through")]
    [TestCase(true, "locked-store")]
    public void NearbyNativeShapesRemainUnproved(bool nested, string defect)
    {
        var body = Body(nested);
        var position = defect switch
        {
            "wrong-parent-load" => 1,
            "wrong-branch" => nested ? 3 : 2,
            "nonzero" or "wide-store" or "narrow-store" or "wrong-receiver" or "locked-store"
                => nested ? 4 : 3,
            "wrong-stack" => nested ? 5 : 4,
            _ => nested ? 7 : 6,
        };
        var instruction = body[position];
        switch (defect)
        {
            case "nonzero":
                if (nested) instruction.Immediate32 = 1;
                else instruction.Immediate8 = 1;
                break;
            case "wide-store": instruction.Code = Code.Mov_rm32_imm32; break;
            case "narrow-store": instruction.Code = Code.Mov_rm8_imm8; break;
            case "wrong-receiver": instruction.MemoryBase = Register.RDX; break;
            case "wrong-parent-load": instruction.MemoryBase = Register.RDX; break;
            case "wrong-branch": instruction.NearBranch64 = body[nested ? 6 : 5].IP; break;
            case "wrong-stack": instruction.Immediate8to64 = 0x20; break;
            case "call-falls-through": instruction.Code = Code.Nopd; break;
            case "locked-store": instruction.HasLockPrefix = true; break;
        }
        body[position] = instruction;
        Assert.That(X86GuardedZeroStoreProof.TryProveShape(body), Is.Null);
    }

    private static List<Instruction> Body(bool nested, bool directInteger = false)
    {
        const ulong address = 0x1000;
        var bytes = Convert.FromHexString(nested
            ? "4883EC28488B41104885C0740CC74010000000004883C428C3E8E23F0000"
            : directInteger ? "4883EC284885C9740CC74110000000004883C428C3E8E23F0000"
            : "4883EC284885C97409C64110004883C428C3E8E93F0000");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
        var body = new List<Instruction>();
        while (decoder.IP < address + (ulong)bytes.Length)
            body.Add(decoder.Decode());
        return body;
    }
}
