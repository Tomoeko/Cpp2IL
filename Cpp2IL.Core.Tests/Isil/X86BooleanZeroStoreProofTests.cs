using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86BooleanZeroStoreProofTests
{
    [Test]
    public void ExactByteZeroStoreHasOneProvedOffset()
    {
        var body = Body();
        Assert.That(X86BooleanZeroStoreProof.TryProveShape(body, out var offset), Is.True);
        Assert.That(offset, Is.EqualTo(0x10));
    }

    [TestCase("nonzero")]
    [TestCase("wide-store")]
    [TestCase("wrong-receiver")]
    [TestCase("wrong-branch")]
    [TestCase("wrong-stack")]
    [TestCase("call-falls-through")]
    [TestCase("locked-store")]
    public void NearbyNativeShapesRemainUnproved(string defect)
    {
        var body = Body();
        var position = defect switch
        {
            "wrong-branch" => 2,
            "nonzero" or "wide-store" or "wrong-receiver" or "locked-store" => 3,
            "wrong-stack" => 4,
            _ => 6,
        };
        var instruction = body[position];
        switch (defect)
        {
            case "nonzero": instruction.Immediate8 = 1; break;
            case "wide-store": instruction.Code = Code.Mov_rm32_imm32; break;
            case "wrong-receiver": instruction.MemoryBase = Register.RDX; break;
            case "wrong-branch": instruction.NearBranch64 = body[5].IP; break;
            case "wrong-stack": instruction.Immediate8to64 = 0x20; break;
            case "call-falls-through": instruction.Code = Code.Nopd; break;
            case "locked-store": instruction.HasLockPrefix = true; break;
        }
        body[position] = instruction;
        Assert.That(X86BooleanZeroStoreProof.TryProveShape(body, out _), Is.False);
    }

    private static List<Instruction> Body()
    {
        const ulong address = 0x1000;
        var bytes = Convert.FromHexString("4883EC284885C97409C64110004883C428C3E8E93F0000");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
        var body = new List<Instruction>();
        while (decoder.IP < address + (ulong)bytes.Length)
            body.Add(decoder.Decode());
        return body;
    }
}
