using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ConditionalCallResultTailProofTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void RepeatedProducerAndConditionalBranchRetainSeparateNullFailures(bool condition, bool value)
    {
        var body = Body(condition, value);
        var shape = X64ConditionalCallResultTailProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(shape!.CallsWhenTrue, Is.EqualTo(condition));
            Assert.That(shape.LiteralValue, Is.EqualTo(value));
            Assert.That(shape.Producer, Is.EqualTo(body[4].NearBranchTarget));
            Assert.That(shape.Predicate, Is.EqualTo(body[9].NearBranchTarget));
            Assert.That(shape.Target, Is.EqualTo(body[22].NearBranchTarget));
        });

        foreach (var index in new[] { 6, 16 })
        {
            var changed = body.ToArray();
            changed[index].NearBranch64 = body[23].IP;
            Assert.That(X64ConditionalCallResultTailProof.TryProveShape(changed), Is.Null,
                "Neither missing receiver may become the conditional early return.");
        }
        var wrongBranch = body.ToArray();
        wrongBranch[11].NearBranch64 = body[26].IP;
        Assert.That(X64ConditionalCallResultTailProof.TryProveShape(wrongBranch), Is.Null);
        var secondProducer = body.ToArray();
        secondProducer[14].NearBranch64 += 1;
        Assert.That(X64ConditionalCallResultTailProof.TryProveShape(secondProducer), Is.Null,
            "Both observable producer calls must name the same original destination.");
        var producerReceiver = body.ToArray();
        producerReceiver[13].Op1Register = Register.RAX;
        Assert.That(X64ConditionalCallResultTailProof.TryProveShape(producerReceiver), Is.Null);
        var tailReceiver = body.ToArray();
        tailReceiver[19].Op1Register = Register.RBX;
        Assert.That(X64ConditionalCallResultTailProof.TryProveShape(tailReceiver), Is.Null,
            "The tail must receive the second producer result.");
        var predicate = body.ToArray();
        predicate[10].Op0Register = Register.EAX;
        Assert.That(X64ConditionalCallResultTailProof.TryProveShape(predicate), Is.Null);
        var metadata = body.ToArray();
        metadata[17].Op0Register = Register.EDX;
        Assert.That(X64ConditionalCallResultTailProof.TryProveShape(metadata), Is.Null);
        var tail = body.ToArray();
        tail[22].Code = Code.Call_rel32_64;
        Assert.That(X64ConditionalCallResultTailProof.TryProveShape(tail), Is.Null);
        foreach (var index in new[] { 20, 23 })
        {
            var stack = body.ToArray();
            stack[index].Immediate8 = 0x28;
            Assert.That(X64ConditionalCallResultTailProof.TryProveShape(stack), Is.Null);
        }
        var restored = body.ToArray();
        restored[24].Op0Register = Register.RBP;
        Assert.That(X64ConditionalCallResultTailProof.TryProveShape(restored), Is.Null);
        if (value)
        {
            body[18].Immediate8 = 2;
            Assert.That(X64ConditionalCallResultTailProof.TryProveShape(body), Is.Null);
        }
    }

    private static Instruction[] Body(bool condition, bool value)
    {
        // These addresses bind no runtime functions. Find independently proves
        // PE bytes, unwind/padding, metadata, unique callees and null helper.
        var bytes = new List<byte>();
        void Add(params byte[] data) => bytes.AddRange(data);
        Add(0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x33, 0xD2, 0x48, 0x8B, 0xD9);
        Add(0xE8, 0xF0, 0, 0, 0, 0x48, 0x85, 0xC0, 0x74, 0x35);
        Add(0x33, 0xD2, 0x48, 0x8B, 0xC8, 0xE8, 0xF0, 1, 0, 0, 0x84, 0xC0);
        Add(condition ? (byte)0x74 : (byte)0x75, 0x21);
        Add(0x33, 0xD2, 0x48, 0x8B, 0xCB, 0xE8, 0xD3, 0, 0, 0, 0x48, 0x85, 0xC0, 0x74, 0x18);
        Add(0x45, 0x33, 0xC0);
        if (value)
            Add(0xB2, 1);
        else
            Add(0x33, 0xD2);
        Add(0x48, 0x8B, 0xC8, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xE9, 0xF0, 2, 0, 0);
        Add(0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3, 0xE8, 0xF0, 3, 0, 0);
        var decoder = Decoder.Create(64, bytes.ToArray(), 0x1000);
        var body = new List<Instruction>();
        while (decoder.IP < 0x1000UL + (ulong)bytes.Count)
            body.Add(decoder.Decode());
        return body.ToArray();
    }
}
