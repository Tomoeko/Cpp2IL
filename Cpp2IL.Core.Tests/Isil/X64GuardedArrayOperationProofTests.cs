using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64GuardedArrayOperationProofTests
{
    [Test]
    public void SharedSignedIndexRequiresEveryGuardWidthFlagAndUse()
    {
        var body = SumBody();
        var evidence = Prove(body);
        Assert.That(evidence, Is.Not.Null);
        Assert.That(evidence!.Sites, Has.Count.EqualTo(2));
        Assert.That(evidence.Sites.Select(site => site.Extension).Distinct().ToArray(), Has.Length.EqualTo(1));

        var signedBounds = body.ToArray();
        signedBounds[evidence.Sites[0].BoundsCompare + 1].Code = Code.Jge_rel8_64;
        Assert.That(Prove(signedBounds), Is.Null, "Negative indices require unsigned bounds semantics.");
        var wrongLength = body.ToArray();
        wrongLength[evidence.Sites[0].BoundsCompare].MemoryDisplacement64 += 4;
        Assert.That(Prove(wrongLength), Is.Null);
        var wrongScale = body.ToArray();
        wrongScale[evidence.Sites[1].Operation].MemoryIndexScale = 8;
        Assert.That(Prove(wrongScale), Is.Null);
        var wrongIndex = body.ToArray();
        wrongIndex[evidence.Sites[0].Extension].Op1Register = Register.EDX;
        Assert.That(Prove(wrongIndex), Is.Null);
        var nullToBounds = body.ToArray();
        nullToBounds[evidence.Sites[0].NullTest + 1].NearBranch64 = body[evidence.BoundsCall].IP;
        Assert.That(Prove(nullToBounds), Is.Null);
        var independentBoundsEntry = body.ToArray();
        independentBoundsEntry[evidence.Sites[1].NullTest + 1].Code = Code.Jmp_rel8_64;
        independentBoundsEntry[evidence.Sites[1].NullTest + 1].NearBranch64 = body[evidence.BoundsCall].IP;
        Assert.That(Prove(independentBoundsEntry), Is.Null);

        var flagsUsedAgain = body.ToArray();
        var addition = Array.FindIndex(body, instruction =>
            instruction.Mnemonic == Mnemonic.Add && instruction.Op0Register == Register.EAX);
        flagsUsedAgain[addition].Code = Code.Adc_r32_rm32;
        Assert.That(Prove(flagsUsedAgain), Is.Null, "A removed bounds compare cannot supply another flag consumer.");
        var helper = body.ToArray();
        helper[evidence.BoundsCall].NearBranch64 = 0x2000;
        Assert.That(Prove(helper), Is.Null);
        var discontinuity = body.ToArray();
        discontinuity[4].IP++;
        Assert.That(Prove(discontinuity), Is.Null);
        var prefix = body.ToArray();
        prefix[evidence.Sites[0].Operation].HasLockPrefix = true;
        Assert.That(Prove(prefix), Is.Null);

        var extra64BitUse = body.ToArray();
        extra64BitUse[evidence.Sites[1].BoundsCompare].Code = Code.Cmp_r64_rm64;
        extra64BitUse[evidence.Sites[1].BoundsCompare].Op0Register = Register.RAX;
        Assert.That(Prove(extra64BitUse), Is.Null,
            "Converting an extended native register to Int32 must not alter another 64-bit use.");
    }

    private static X64GuardedArrayOperationProof.NativeEvidence? Prove(Instruction[] body) =>
        X64GuardedArrayOperationProof.TryProveNative(body, target => target == 0x2000,
            target => target == 0x3000);

    private static Instruction[] SumBody()
    {
        var bytes = new List<byte>();
        var branches = new List<(int Offset, bool Bounds)>();
        void Add(params byte[] value) => bytes.AddRange(value);
        void Guard(byte opcode, bool bounds)
        {
            Add(opcode, 0);
            branches.Add((bytes.Count - 1, bounds));
        }
        void Call(int target)
        {
            Add(0xE8);
            Add(BitConverter.GetBytes(target - (0x1000 + bytes.Count + 4)));
        }
        Add(0x48, 0x83, 0xEC, 0x28);
        Add(0x48, 0x85, 0xC9); Guard(0x74, false);
        Add(0x44, 0x3B, 0x41, 0x18); Guard(0x73, true);
        Add(0x49, 0x63, 0xC0);
        Add(0x44, 0x8B, 0x4C, 0x81, 0x20);
        Add(0x48, 0x85, 0xD2); Guard(0x74, false);
        Add(0x44, 0x3B, 0x42, 0x18); Guard(0x73, true);
        Add(0x8B, 0x44, 0x82, 0x20);
        Add(0x41, 0x03, 0xC1, 0x48, 0x83, 0xC4, 0x28, 0xC3);
        var nullOffset = bytes.Count; Call(0x2000); Add(0xCC);
        var boundsOffset = bytes.Count; Call(0x3000); Add(0xCC);
        foreach (var branch in branches)
            bytes[branch.Offset] = checked((byte)((branch.Bounds ? boundsOffset : nullOffset) - branch.Offset - 1));
        var decoder = Decoder.Create(64, bytes.ToArray(), 0x1000);
        var result = new List<Instruction>();
        while (decoder.IP < 0x1000UL + (ulong)bytes.Count)
            result.Add(decoder.Decode());
        return result.ToArray();
    }
}
