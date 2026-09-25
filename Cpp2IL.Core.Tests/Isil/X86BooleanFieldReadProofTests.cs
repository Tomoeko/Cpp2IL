using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86BooleanFieldReadProofTests
{
    [Test]
    public void ExactNullGuardedByteLoadRetainsTheFieldOffsetAndLoadAddress()
    {
        var body = Body();
        var shape = X86BooleanFieldReadProof.TryProveShape(body);
        var secondArgument = X86BooleanFieldReadProof.TryProveShape(Body(Register.RDX));
        Assert.Multiple(() =>
        {
            Assert.That(shape, Is.Not.Null);
            Assert.That(shape!.FieldOffset, Is.EqualTo(0x10));
            Assert.That(shape.LoadIp, Is.EqualTo(body[3].IP));
            Assert.That(shape.CallIndex, Is.EqualTo(6));
            Assert.That(shape.ReceiverRegister, Is.EqualTo(Register.RCX));
            Assert.That(secondArgument, Is.Not.Null);
            Assert.That(secondArgument!.ReceiverRegister, Is.EqualTo(Register.RDX));
            Assert.That(secondArgument.FieldOffset, Is.EqualTo(0x10));
        });
    }

    [Test]
    public void SecondArgumentLoadMustUseTheGuardedRegister()
    {
        var body = Body(Register.RDX);
        var load = body[3];
        load.MemoryBase = Register.RCX;
        body[3] = load;
        Assert.That(X86BooleanFieldReadProof.TryProveShape(body), Is.Null);
    }

    [Test]
    public void TrailingNativeWorkIsNotAcceptedAsPartOfTheGetter()
    {
        var body = Body(Register.RDX);
        body.Add(Decode("90", body[^1].NextIP));
        Assert.That(X86BooleanFieldReadProof.TryProveShape(body), Is.Null);
    }

    [TestCase("wrong-test")]
    [TestCase("wrong-branch")]
    [TestCase("wrong-register")]
    [TestCase("wrong-base")]
    [TestCase("wide-source")]
    [TestCase("wrong-stack")]
    [TestCase("return-adjusts-stack")]
    [TestCase("call-falls-through")]
    [TestCase("locked-load")]
    public void NearbyNativeShapesRemainUnproved(string defect)
    {
        var body = Body();
        var index = defect switch
        {
            "wrong-test" => 1,
            "wrong-branch" => 2,
            "wrong-register" or "wrong-base" or "wide-source" or "locked-load" => 3,
            "wrong-stack" => 4,
            "return-adjusts-stack" => 5,
            _ => 6,
        };
        var instruction = body[index];
        switch (defect)
        {
            case "wrong-test": instruction.Op0Register = Register.RDX; break;
            case "wrong-branch": instruction.NearBranch64 = body[5].IP; break;
            case "wrong-register": instruction.Op0Register = Register.EDX; break;
            case "wrong-base": instruction.MemoryBase = Register.RDX; break;
            case "wide-source": instruction.Code = Code.Movzx_r32_rm16; break;
            case "wrong-stack": instruction.Immediate8to64 = 0x20; break;
            case "return-adjusts-stack": instruction.Code = Code.Retnw_imm16; break;
            case "call-falls-through": instruction.Code = Code.Nopd; break;
            case "locked-load": instruction.HasLockPrefix = true; break;
        }
        body[index] = instruction;
        Assert.That(X86BooleanFieldReadProof.TryProveShape(body), Is.Null);
    }

    private static List<Instruction> Body(Register receiver = Register.RCX)
    {
        const ulong address = 0x1000;
        var bytes = Convert.FromHexString(receiver == Register.RDX
            ? "4883EC284885D274090FB642104883C428C3E8E93F0000"
            : "4883EC284885C974090FB641104883C428C3E8E93F0000");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
        var body = new List<Instruction>();
        while (decoder.IP < address + (ulong)bytes.Length)
            body.Add(decoder.Decode());
        return body;
    }

    private static Instruction Decode(string hex, ulong address)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(hex)), address);
        return decoder.Decode();
    }
}
