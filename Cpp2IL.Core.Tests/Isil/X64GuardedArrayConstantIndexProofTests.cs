using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64GuardedArrayConstantIndexProofTests
{
    [Test]
    public void FirstReferenceArgumentHasNoInventedIndexExtension()
    {
        var evidence = Prove(Body());
        Assert.That(evidence, Is.Not.Null);
        Assert.That(evidence!.Sites, Has.Count.EqualTo(1));
        var site = evidence.Sites[0];
        Assert.That(site.ConstantIndex, Is.Zero);
        Assert.That(site.Extension, Is.EqualTo(-1));
        Assert.That(site.IndexRegister, Is.EqualTo(Register.None));
        Assert.That(site.IndexSource, Is.EqualTo(Register.None));
        Assert.That(site.Width, Is.EqualTo(8));
        Assert.That(evidence.Effects, Is.EqualTo(new[] { 1, 6, 9 }));
    }

    [TestCase("signed-bounds")]
    [TestCase("nonzero-index")]
    [TestCase("negative-index")]
    [TestCase("wrong-length")]
    [TestCase("wrong-element")]
    [TestCase("narrow-element")]
    [TestCase("indexed-element")]
    [TestCase("wrong-array-origin")]
    [TestCase("wrong-null-value")]
    [TestCase("wrong-null-arm")]
    [TestCase("wrong-bounds-arm")]
    [TestCase("method-info")]
    [TestCase("receiver-clobber")]
    [TestCase("bad-frame")]
    [TestCase("internal-tail")]
    [TestCase("wrong-null-helper")]
    [TestCase("wrong-bounds-helper")]
    [TestCase("missing-null-trap")]
    [TestCase("missing-bounds-trap")]
    [TestCase("prefix")]
    [TestCase("discontinuity")]
    public void ClosedShapeRejectsUnprovedIndexGuardWidthAndEffects(string mutation)
    {
        var body = Body();
        switch (mutation)
        {
            case "signed-bounds": body[5].Code = Code.Jle_rel8_64; break;
            case "nonzero-index": body[4].Immediate8 = 1; break;
            case "negative-index": body[4].Immediate8 = 255; break;
            case "wrong-length": body[4].MemoryDisplacement64 = 0x1C; break;
            case "wrong-element": body[6].MemoryDisplacement64 = 0x28; break;
            case "narrow-element": body[6].Code = Code.Mov_r32_rm32; body[6].Op0Register = Register.EDX; break;
            case "indexed-element": body[6].MemoryIndex = Register.RAX; body[6].MemoryIndexScale = 8; break;
            case "wrong-array-origin": body[1].MemoryBase = Register.R8; break;
            case "wrong-null-value": body[2].Op0Register = Register.RCX; body[2].Op1Register = Register.RCX; break;
            case "wrong-null-arm": body[3].NearBranch64 = body[12].IP; break;
            case "wrong-bounds-arm": body[5].NearBranch64 = body[10].IP; break;
            case "method-info": body[7].Op1Register = Register.EAX; break;
            case "receiver-clobber": body[7].Op0Register = Register.ECX; body[7].Op1Register = Register.ECX; break;
            case "bad-frame": body[8].Immediate8 = 0x30; break;
            case "internal-tail": body[9].NearBranch64 = body[6].IP; break;
            case "wrong-null-helper": body[10].NearBranch64 = 0x3000; break;
            case "wrong-bounds-helper": body[12].NearBranch64 = 0x2000; break;
            case "missing-null-trap": body[11].Code = Code.Nopd; break;
            case "missing-bounds-trap": body[13].Code = Code.Nopd; break;
            case "prefix": body[6].HasRepPrefix = true; break;
            case "discontinuity": body[6].IP++; break;
            default: throw new ArgumentException(mutation);
        }
        Assert.That(Prove(body), Is.Null, mutation);
    }

    private static X64GuardedArrayOperationProof.NativeEvidence? Prove(Instruction[] body) =>
        X64GuardedArrayOperationProof.TryProveNative(body, target => target == 0x2000,
            target => target == 0x3000);

    private static Instruction[] Body()
    {
        // Neutral instructions assembled here, not a player byte snapshot.
        var bytes = new List<byte>();
        void Add(params byte[] value) => bytes.AddRange(value);
        void Call(int target)
        {
            Add(0xE8);
            Add(BitConverter.GetBytes(target - (0x1000 + bytes.Count + 4)));
        }
        Add(0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0x51, 0x10, 0x48, 0x85, 0xD2);
        Add(0x74, 0x16, 0x83, 0x7A, 0x18, 0, 0x76, 0x16);
        Add(0x48, 0x8B, 0x52, 0x20, 0x45, 0x33, 0xC0, 0x48, 0x83, 0xC4, 0x28);
        Add(0xE9); Add(BitConverter.GetBytes(0x4000 - (0x1000 + bytes.Count + 4)));
        Call(0x2000); Add(0xCC); Call(0x3000); Add(0xCC);
        var decoder = Decoder.Create(64, bytes.ToArray(), 0x1000);
        var body = new List<Instruction>();
        while (decoder.IP < 0x1000UL + (ulong)bytes.Count) body.Add(decoder.Decode());
        return body.ToArray();
    }
}
