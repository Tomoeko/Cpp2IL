using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64CallResultBooleanTailProofTests
{
    [Test]
    public void ParameterShapeRequiresBothGuardsAndTheFalseTailAbi()
    {
        // Synthetic rel32 calls identify no real runtime functions. The
        // structural predicate cannot authorize recovery without Find's
        // player metadata, PE/unwind and runtime-helper proofs.
        var body = Decode(
        [
            0x48, 0x83, 0xEC, 0x28,
            0x48, 0x85, 0xC9,
            0x74, 0x1D,
            0x33, 0xD2,
            0xE8, 0xF0, 0x00, 0x00, 0x00,
            0x48, 0x85, 0xC0,
            0x74, 0x11,
            0x45, 0x33, 0xC0,
            0x33, 0xD2,
            0x48, 0x8B, 0xC8,
            0x48, 0x83, 0xC4, 0x28,
            0xE9, 0xD7, 0x01, 0x00, 0x00,
            0xE8, 0xD2, 0x02, 0x00, 0x00
        ]);
        Assert.That(body, Has.Length.EqualTo(13));
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(body, true), Is.Not.Null);
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(body, false), Is.Null);

        foreach (var index in new[] { 2, 6 })
        {
            var changed = body.ToArray();
            changed[index].NearBranch64 = body[11].IP;
            Assert.That(X64CallResultBooleanTailProof.TryProveShape(changed, true), Is.Null,
                "Both null branches must exclusively reach the nonreturning helper call.");
        }

        var metadata = body.ToArray();
        metadata[7].Op0Register = Register.EDX;
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(metadata, true), Is.Null);
        var boolean = body.ToArray();
        boolean[8].Op0Register = Register.R8D;
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(boolean, true), Is.Null);
        var receiver = body.ToArray();
        receiver[9].Op1Register = Register.RDX;
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(receiver, true), Is.Null);
        var tail = body.ToArray();
        tail[11].Code = Code.Call_rel32_64;
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(tail, true), Is.Null);
        var stack = body.ToArray();
        stack[10].Immediate8 = 0x20;
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(stack, true), Is.Null);
    }

    private static Instruction[] Decode(byte[] bytes)
    {
        var decoder = Decoder.Create(64, bytes, 0x1000);
        var instructions = new List<Instruction>();
        while (decoder.IP < 0x1000UL + (ulong)bytes.Length)
            instructions.Add(decoder.Decode());
        return instructions.ToArray();
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void LiteralValueAndReceiverGuardRemainIndependent(bool checksReceiver, bool value)
    {
        var body = Body(checksReceiver, parameter: false, value);
        var shift = checksReceiver ? 2 : 0;
        var shape = X64CallResultBooleanTailProof.TryProveShape(body, checksReceiver);
        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.LiteralValue, Is.EqualTo(value));
        Assert.That(shape.UsesParameter, Is.False);
        if (value)
        {
            body[6 + shift].Immediate8 = 2;
            Assert.That(X64CallResultBooleanTailProof.TryProveShape(body, checksReceiver), Is.Null,
                "The Boolean literal must have canonical true bits.");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ParameterByteMustSurviveTheProducerInSavedRbx(bool checksReceiver)
    {
        var body = Body(checksReceiver, parameter: true, value: false);
        var shift = checksReceiver ? 2 : 0;
        var shape = X64CallResultBooleanTailProof.TryProveShape(body, checksReceiver, true);
        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.UsesParameter, Is.True);
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(body, checksReceiver), Is.Null);

        foreach (var index in new[] { 2, 8 + shift })
        {
            var changed = body.ToArray();
            changed[index].Op1Register = Register.AL;
            Assert.That(X64CallResultBooleanTailProof.TryProveShape(changed, checksReceiver, true), Is.Null,
                "Only the incoming DL byte saved before the producer may reach the tail argument.");
        }
        var stack = body.ToArray();
        stack[10 + shift].Immediate8 = 0x28;
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(stack, checksReceiver, true), Is.Null);
        var guard = body.ToArray();
        guard[6 + shift].NearBranch64 = body[12 + shift].IP;
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(guard, checksReceiver, true), Is.Null);
        var metadata = body.ToArray();
        metadata[7 + shift].Op0Register = Register.EDX;
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(metadata, checksReceiver, true), Is.Null);
        var restore = body.ToArray();
        restore[11 + shift].Op0Register = Register.RBP;
        Assert.That(X64CallResultBooleanTailProof.TryProveShape(restore, checksReceiver, true), Is.Null);
    }

    private static Instruction[] Body(bool checksReceiver, bool parameter, bool value)
    {
        // Synthetic destinations and computed local guard offsets establish only
        // structure. The optional player tests independently authenticate Find.
        var bytes = new List<byte>();
        var branches = new List<int>();
        void Add(params byte[] values) => bytes.AddRange(values);
        void Guard()
        {
            branches.Add(bytes.Count);
            Add(0x74, 0);
        }
        if (parameter)
            Add(0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x0F, 0xB6, 0xDA);
        else
            Add(0x48, 0x83, 0xEC, 0x28);
        if (checksReceiver)
        {
            Add(0x48, 0x85, 0xC9);
            Guard();
        }
        Add(0x33, 0xD2, 0xE8, 0xF0, 0, 0, 0, 0x48, 0x85, 0xC0);
        Guard();
        Add(0x45, 0x33, 0xC0);
        if (parameter)
            Add(0x0F, 0xB6, 0xD3);
        else if (value)
            Add(0xB2, 1);
        else
            Add(0x33, 0xD2);
        Add(0x48, 0x8B, 0xC8, 0x48, 0x83, 0xC4, parameter ? (byte)0x20 : (byte)0x28);
        if (parameter)
            Add(0x5B);
        Add(0xE9, 0xF0, 1, 0, 0);
        var nullExit = bytes.Count;
        Add(0xE8, 0xF0, 2, 0, 0);
        foreach (var branch in branches)
            bytes[branch + 1] = checked((byte)(nullExit - branch - 2));
        return Decode(bytes.ToArray());
    }
}
