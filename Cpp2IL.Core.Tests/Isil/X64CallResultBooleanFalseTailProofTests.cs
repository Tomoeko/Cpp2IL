using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64CallResultBooleanFalseTailProofTests
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
        Assert.That(X64CallResultBooleanFalseTailProof.TryProveShape(body, true), Is.Not.Null);
        Assert.That(X64CallResultBooleanFalseTailProof.TryProveShape(body, false), Is.Null);

        foreach (var index in new[] { 2, 6 })
        {
            var changed = body.ToArray();
            changed[index].NearBranch64 = body[11].IP;
            Assert.That(X64CallResultBooleanFalseTailProof.TryProveShape(changed, true), Is.Null,
                "Both null branches must exclusively reach the nonreturning helper call.");
        }

        var metadata = body.ToArray();
        metadata[7].Op0Register = Register.EDX;
        Assert.That(X64CallResultBooleanFalseTailProof.TryProveShape(metadata, true), Is.Null);
        var boolean = body.ToArray();
        boolean[8].Op0Register = Register.R8D;
        Assert.That(X64CallResultBooleanFalseTailProof.TryProveShape(boolean, true), Is.Null);
        var receiver = body.ToArray();
        receiver[9].Op1Register = Register.RDX;
        Assert.That(X64CallResultBooleanFalseTailProof.TryProveShape(receiver, true), Is.Null);
        var tail = body.ToArray();
        tail[11].Code = Code.Call_rel32_64;
        Assert.That(X64CallResultBooleanFalseTailProof.TryProveShape(tail, true), Is.Null);
        var stack = body.ToArray();
        stack[10].Immediate8 = 0x20;
        Assert.That(X64CallResultBooleanFalseTailProof.TryProveShape(stack, true), Is.Null);
    }

    private static Instruction[] Decode(byte[] bytes)
    {
        var decoder = Decoder.Create(64, bytes, 0x1000);
        var instructions = new List<Instruction>();
        while (decoder.IP < 0x1000UL + (ulong)bytes.Length)
            instructions.Add(decoder.Decode());
        return instructions.ToArray();
    }
}
