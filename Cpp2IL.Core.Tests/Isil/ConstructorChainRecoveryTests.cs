using System;
using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class ConstructorChainRecoveryTests
{
    [Test]
    public void ExactSharedConstructorTailThunkHasAnExternalTarget()
    {
        var body = Body();
        Assert.That(ConstructorChainRecovery.TryProveShape(body, 0x1000, 7,
            body[1].NearBranchTarget), Is.True);
    }

    [Test]
    public void SharedTailThunkCanEndBeforeAnOverestimatedMetadataSpan()
    {
        var body = Body();
        var padded = Convert.FromHexString("CCCC");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(padded), body[1].NextIP);
        body.Add(decoder.Decode());
        body.Add(decoder.Decode());
        Assert.That(ConstructorChainRecovery.TryProveShape(body, 0x1000, 9,
            body[1].NearBranchTarget), Is.True);
    }

    [TestCase("wrong-clear-register")]
    [TestCase("wrong-clear-source")]
    [TestCase("call-instead-of-tail")]
    [TestCase("wrong-target")]
    [TestCase("target-inside-body")]
    [TestCase("truncated-span")]
    [TestCase("locked-instruction")]
    public void NeighboringNativeBodiesDoNotProveAConstructorChain(string defect)
    {
        var body = Body();
        var target = body[1].NearBranchTarget;
        var length = 7;
        var instruction = body[defect is "wrong-clear-register" or "wrong-clear-source" or
            "locked-instruction" ? 0 : 1];
        switch (defect)
        {
            case "wrong-clear-register": instruction.Op0Register = Register.ECX; break;
            case "wrong-clear-source": instruction.Op1Register = Register.EAX; break;
            case "call-instead-of-tail": instruction.Code = Code.Call_rel32_64; break;
            case "wrong-target": target++; break;
            case "target-inside-body": target = body[1].IP; break;
            case "truncated-span": length--; break;
            case "locked-instruction": instruction.HasLockPrefix = true; break;
        }
        body[defect is "wrong-clear-register" or "wrong-clear-source" or
            "locked-instruction" ? 0 : 1] = instruction;
        Assert.That(ConstructorChainRecovery.TryProveShape(body, 0x1000, length, target), Is.False);
    }

    private static List<Instruction> Body()
    {
        const ulong address = 0x1000;
        var bytes = Convert.FromHexString("33D2E911000000");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
        var body = new List<Instruction>();
        while (decoder.IP < address + (ulong)bytes.Length)
            body.Add(decoder.Decode());
        return body;
    }
}
