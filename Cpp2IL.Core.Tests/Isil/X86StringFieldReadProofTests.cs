using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86StringFieldReadProofTests
{
    [Test]
    public void ExactDirectAndNestedReferenceReadsRetainTheirFieldOffsets()
    {
        var direct = X86StringFieldReadProof.TryProveShape(Body(false));
        var nested = X86StringFieldReadProof.TryProveShape(Body(true));
        Assert.Multiple(() =>
        {
            Assert.That(direct, Is.Not.Null);
            Assert.That(direct!.ReceiverOffset, Is.Null);
            Assert.That(direct.FieldOffset, Is.EqualTo(0x10));
            Assert.That(nested, Is.Not.Null);
            Assert.That(nested!.ReceiverOffset, Is.EqualTo(0x10));
            Assert.That(nested.FieldOffset, Is.EqualTo(0x10));
        });
    }

    [TestCase(false, "narrow-load")]
    [TestCase(false, "wrong-receiver")]
    [TestCase(false, "wrong-branch")]
    [TestCase(false, "wrong-stack")]
    [TestCase(false, "missing-helper")]
    [TestCase(false, "prefix")]
    [TestCase(true, "narrow-load")]
    [TestCase(true, "wrong-receiver")]
    [TestCase(true, "wrong-parent")]
    [TestCase(true, "wrong-branch")]
    [TestCase(true, "wrong-stack")]
    [TestCase(true, "missing-helper")]
    [TestCase(true, "prefix")]
    public void NearbyNativeShapesRemainUnproved(bool nested, string defect)
    {
        var body = Body(nested);
        var position = defect switch
        {
            "wrong-parent" => 1,
            "wrong-branch" => nested ? 3 : 2,
            "narrow-load" or "wrong-receiver" or "prefix" => nested ? 4 : 3,
            "wrong-stack" => nested ? 5 : 4,
            _ => body.Count - 1,
        };
        var instruction = body[position];
        switch (defect)
        {
            case "narrow-load": instruction.Code = Code.Mov_r32_rm32; break;
            case "wrong-receiver": instruction.MemoryBase = Register.RDX; break;
            case "wrong-parent": instruction.MemoryBase = Register.RDX; break;
            case "wrong-branch": instruction.NearBranch64 = body[^2].IP; break;
            case "wrong-stack": instruction.Immediate8to64 = 0x20; break;
            case "missing-helper": instruction.Code = Code.Nopd; break;
            case "prefix": instruction.HasLockPrefix = true; break;
        }
        body[position] = instruction;
        Assert.That(X86StringFieldReadProof.TryProveShape(body), Is.Null);
    }

    private static List<Instruction> Body(bool nested)
    {
        const ulong address = 0x1000;
        var bytes = Convert.FromHexString(nested
            ? "4883EC28488B41104885C07409488B40104883C428C3E811000000"
            : "4883EC284885C97409488B41104883C428C3E811000000");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
        var body = new List<Instruction>();
        while (decoder.IP < address + (ulong)bytes.Length)
            body.Add(decoder.Decode());
        return body;
    }
}
