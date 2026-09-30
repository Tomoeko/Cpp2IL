using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64BaseEffectBooleanTailProofTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ExactSequenceRequiresOrderedCallsGuardReceiverAndManagedBooleanAbi(bool value)
    {
        // Neutral rel32 destinations are structural witnesses, not authenticated
        // runtime or managed identities. Find independently proves those inputs.
        var body = Decode(value);
        var shape = X64BaseEffectBooleanTailProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.Value, Is.EqualTo(value));
        Assert.That(shape.Effect, Is.EqualTo(body[4].NearBranchTarget));
        Assert.That(shape.Producer, Is.EqualTo(body[7].NearBranchTarget));

        var guard = body.ToArray();
        guard[9].NearBranch64 = body[15].IP;
        Assert.That(X64BaseEffectBooleanTailProof.TryProveShape(guard), Is.Null,
            "A null result must exclusively enter the nonreturning helper arm.");
        var metadata = body.ToArray();
        metadata[10].Op0Register = Register.R9D;
        Assert.That(X64BaseEffectBooleanTailProof.TryProveShape(metadata), Is.Null);
        var receiver = body.ToArray();
        receiver[12].Op1Register = Register.RBX;
        Assert.That(X64BaseEffectBooleanTailProof.TryProveShape(receiver), Is.Null,
            "The guarded call uses the producer result, not the caller instance.");
        var producerReceiver = body.ToArray();
        producerReceiver[6].Op1Register = Register.RAX;
        Assert.That(X64BaseEffectBooleanTailProof.TryProveShape(producerReceiver), Is.Null);
        var indirect = body.ToArray();
        indirect[4].Code = Code.Call_rm64;
        Assert.That(X64BaseEffectBooleanTailProof.TryProveShape(indirect), Is.Null);
        var stack = body.ToArray();
        stack[13].Immediate8 = 0x28;
        Assert.That(X64BaseEffectBooleanTailProof.TryProveShape(stack), Is.Null);
        var continuity = body.ToArray();
        continuity[7].IP++;
        Assert.That(X64BaseEffectBooleanTailProof.TryProveShape(continuity), Is.Null);
        var prefix = body.ToArray();
        prefix[7].HasLockPrefix = true;
        Assert.That(X64BaseEffectBooleanTailProof.TryProveShape(prefix), Is.Null);
        if (value)
        {
            var nonBoolean = body.ToArray();
            nonBoolean[11].Immediate8 = 2;
            Assert.That(X64BaseEffectBooleanTailProof.TryProveShape(nonBoolean), Is.Null,
                "A noncanonical byte cannot be promoted to an evidenced Boolean literal.");
        }
    }

    private static Instruction[] Decode(bool value)
    {
        byte[] bytes =
        [
            0x40, 0x53, 0x48, 0x83, 0xEC, 0x20,
            0x33, 0xD2, 0x48, 0x8B, 0xD9,
            0xE8, 0xF0, 0x00, 0x00, 0x00,
            0x33, 0xD2, 0x48, 0x8B, 0xCB,
            0xE8, 0xF0, 0x01, 0x00, 0x00,
            0x48, 0x85, 0xC0, 0x74, 0x12,
            0x45, 0x33, 0xC0,
            0x33, 0xD2,
            0x48, 0x8B, 0xC8, 0x48, 0x83, 0xC4, 0x20, 0x5B,
            0xE9, 0xF0, 0x02, 0x00, 0x00,
            0xE8, 0xF0, 0x03, 0x00, 0x00
        ];
        if (value)
        {
            bytes[34] = 0xB2;
            bytes[35] = 1;
        }
        var decoder = Decoder.Create(64, bytes, 0x1000);
        var instructions = new List<Instruction>();
        while (decoder.IP < 0x1000UL + (ulong)bytes.Length)
            instructions.Add(decoder.Decode());
        return instructions.ToArray();
    }
}
