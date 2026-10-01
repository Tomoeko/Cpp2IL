using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64GetterReferenceArgumentProofTests
{
    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 3)]
    [TestCase(true, 3)]
    public void CompletePureLeafRetainsItsFieldOffset(bool paddedEntry, int trailingTraps)
    {
        var bytes = new List<byte>();
        if (paddedEntry) bytes.AddRange([0x66, 0x90]);
        bytes.AddRange([0x48, 0x8B, 0x41, 0x10, 0xC3]);
        bytes.AddRange(Enumerable.Repeat((byte)0xCC, trailingTraps));
        Assert.That(X64GetterReferenceArgumentProof.TryProveGetterLeaf(Decode(bytes), out var offset), Is.True);
        Assert.That(offset, Is.EqualTo(16));
    }

    [TestCase("90488B4110C3")]
    [TestCase("66906690488B4110C3")]
    [TestCase("F3488B4110C3")]
    [TestCase("64488B4110C3")]
    [TestCase("67488B4110C3")]
    [TestCase("488B441110C3")]
    [TestCase("488B5110C3")]
    [TestCase("488B4111C3")]
    [TestCase("488B411048FFC0C3")]
    [TestCase("488B4110C3C3")]
    [TestCase("488B4110C3CC90")]
    [TestCase("488B4110")]
    public void PureLeafRejectsUnprovedPrefixAddressValueAndExit(string hex)
    {
        Assert.That(X64GetterReferenceArgumentProof.TryProveGetterLeaf(Decode(Convert.FromHexString(hex)), out _), Is.False);
    }

    [TestCase(0)]
    [TestCase(4)]
    public void ClosedCallerPreservesSourceThenTargetThenPayload(int trailingTraps)
    {
        var body = Caller(trailingTraps);
        var proof = X64GetterReferenceArgumentProof.TryProveShape(body);
        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.SourceOffset, Is.EqualTo(16));
            Assert.That(proof.TargetOffset, Is.EqualTo(24));
            Assert.That(proof.PayloadOffset, Is.EqualTo(16));
            Assert.That(proof.NullCall.IP, Is.EqualTo(body[11].IP));
            Assert.That(proof.Invocation.IP, Is.EqualTo(body[10].IP));
        });
    }

    [TestCase("source-register")]
    [TestCase("source-check")]
    [TestCase("source-guard")]
    [TestCase("target-check")]
    [TestCase("target-guard")]
    [TestCase("payload-owner")]
    [TestCase("payload-width")]
    [TestCase("metadata-register")]
    [TestCase("frame")]
    [TestCase("internal-tail")]
    [TestCase("helper-call")]
    [TestCase("missing-trap")]
    [TestCase("continuity")]
    public void CallerRejectsChangedGuardOrderAbiAndNonreturnExit(string mutation)
    {
        var body = Caller(0);
        var changed = mutation switch
        {
            "source-register" or "source-check" => 2,
            "source-guard" => 3,
            "target-check" => 5,
            "target-guard" => 6,
            "payload-owner" or "payload-width" => 7,
            "metadata-register" => 8,
            "frame" => 9,
            "internal-tail" => 10,
            "helper-call" => 11,
            _ => 12,
        };
        var instruction = body[changed];
        switch (mutation)
        {
            case "source-register": instruction.Op0Register = Register.RCX; break;
            case "source-check": instruction.Code = Code.Cmp_rm64_r64; break;
            case "source-guard": instruction.NearBranch64 = body[6].IP; break;
            case "target-check": instruction.Op1Register = Register.RDX; break;
            case "target-guard": instruction.Code = Code.Jne_rel8_64; break;
            case "payload-owner": instruction.MemoryBase = Register.RCX; break;
            case "payload-width": instruction.Code = Code.Mov_r32_rm32; instruction.Op0Register = Register.EDX; break;
            case "metadata-register": instruction.Op1Register = Register.R9D; break;
            case "frame": instruction.Immediate8 = 32; break;
            case "internal-tail": instruction.NearBranch64 = body[7].IP; break;
            case "helper-call": instruction.Code = Code.Jmp_rel32_64; break;
            case "missing-trap": instruction.Code = Code.Nopd; break;
            case "continuity": instruction.IP++; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        body[changed] = instruction;
        Assert.That(X64GetterReferenceArgumentProof.TryProveShape(body), Is.Null);
    }

    private static Instruction[] Caller(int trailingTraps)
    {
        const ulong start = 0x1000;
        var bytes = new List<byte>();
        bytes.AddRange([0x48, 0x83, 0xEC, 0x28]);
        bytes.AddRange([0x48, 0x8B, 0x51, 0x10]);
        bytes.AddRange([0x48, 0x85, 0xD2, 0x74, 0x19]);
        bytes.AddRange([0x48, 0x8B, 0x49, 0x18]);
        bytes.AddRange([0x48, 0x85, 0xC9, 0x74, 0x10]);
        bytes.AddRange([0x48, 0x8B, 0x52, 0x10]);
        bytes.AddRange([0x45, 0x33, 0xC0]);
        bytes.AddRange([0x48, 0x83, 0xC4, 0x28]);
        AddBranch(0xE9, 0x2000);
        AddBranch(0xE8, 0x3000);
        bytes.Add(0xCC);
        bytes.AddRange(Enumerable.Repeat((byte)0xCC, trailingTraps));
        return Decode(bytes);

        void AddBranch(byte opcode, ulong target)
        {
            var next = start + (ulong)bytes.Count + 5;
            bytes.Add(opcode);
            bytes.AddRange(BitConverter.GetBytes(checked((int)(target - next))));
        }
    }

    private static Instruction[] Decode(IEnumerable<byte> bytes) =>
        X86Utils.Iterate(bytes.ToArray(), 0x1000, false).ToArray();
}
