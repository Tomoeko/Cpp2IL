using System;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86IncrementedBooleanFieldReadProofTests
{
    [Test]
    public void BooleanReadSiteRetainsThePrecedingInt32Effect()
    {
        var body = Body();
        var shape = X86BooleanFieldReadProof.TryProveIncrementedInstanceShape(body);
        Assert.Multiple(() =>
        {
            Assert.That(shape, Is.Not.Null);
            Assert.That(shape!.CounterOffset, Is.EqualTo(0x20));
            Assert.That(shape.FieldOffset, Is.EqualTo(0x18));
            Assert.That(shape.LoadIp, Is.EqualTo(body[1].IP));
        });
        Assert.That(X86BooleanFieldReadProof.TryProveIncrementedInstanceShape(
            body.Concat(new[] { body[2] }).ToArray()), Is.Null,
            "The shape proves the complete leaf rather than dropping trailing native work.");
    }

    [TestCase("counter-width")]
    [TestCase("counter-receiver")]
    [TestCase("counter-index")]
    [TestCase("locked-counter")]
    [TestCase("load-width")]
    [TestCase("load-receiver")]
    [TestCase("load-result")]
    [TestCase("adjusted-return")]
    [TestCase("discontiguous")]
    public void SimilarLeavesDoNotEstablishTheSameFieldRead(string defect)
    {
        var body = Body();
        var index = defect.StartsWith("load", StringComparison.Ordinal) ? 1 :
            defect is "adjusted-return" or "discontiguous" ? 2 : 0;
        var instruction = body[index];
        switch (defect)
        {
            case "counter-width": instruction.Code = Code.Inc_rm8; break;
            case "counter-receiver": instruction.MemoryBase = Register.RDX; break;
            case "counter-index": instruction.MemoryIndex = Register.RAX; break;
            case "locked-counter": instruction.HasLockPrefix = true; break;
            case "load-width": instruction.Code = Code.Movzx_r32_rm16; break;
            case "load-receiver": instruction.MemoryBase = Register.RDX; break;
            case "load-result": instruction.Op0Register = Register.EDX; break;
            case "adjusted-return": instruction.Code = Code.Retnq_imm16; break;
            case "discontiguous": instruction.IP++; break;
        }
        body[index] = instruction;
        Assert.That(X86BooleanFieldReadProof.TryProveIncrementedInstanceShape(body), Is.Null);
    }

    private static Instruction[] Body()
    {
        const ulong address = 0x1000;
        var bytes = Convert.FromHexString("FF41200FB64118C3");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
        return Enumerable.Range(0, 3).Select(_ => decoder.Decode()).ToArray();
    }
}
