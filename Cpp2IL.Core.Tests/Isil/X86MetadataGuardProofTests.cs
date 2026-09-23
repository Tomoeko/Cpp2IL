using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86MetadataGuardProofTests
{
    private const ulong Base = 0x1000;
    private const ulong Flag = 0x3000;
    private const ulong Slot = 0x4000;
    private const ulong Helper = 0x5000;

    [Test]
    public void ExactGuardRetainsHelperAndLiteralMaterializationEvidence()
    {
        var body = LiteralBody();
        var proof = X86MetadataGuardProof.Find(body, new HashSet<ulong> { Helper }, address => address == Slot);
        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.GuardAddress, Is.EqualTo(body[1].IP));
            Assert.That(proof.HelperAddress, Is.EqualTo(Helper));
            Assert.That(proof.LiteralSlot, Is.EqualTo(Slot));
            Assert.That(proof.MaterializationAddress, Is.EqualTo(body[6].IP));
            Assert.That(proof.RemovedAddresses, Is.EqualTo(body.Skip(1).Take(5).Select(instruction => instruction.IP)));
        });
    }

    [Test]
    public void ClosedEntryPathCanIgnoreUnreachablePaddingAndAdjacentCode()
    {
        var body = LiteralBody();
        AppendUnreachableCode(body);
        var proof = X86MetadataGuardProof.Find(body, new HashSet<ulong> { Helper }, address => address == Slot);
        Assert.That(proof, Is.Not.Null);
        Assert.That(proof!.RemovedAddresses, Has.Count.EqualTo(5));
    }

    [TestCase("unknown-helper")]
    [TestCase("not-literal")]
    [TestCase("condition-not-zero")]
    [TestCase("wide-condition")]
    [TestCase("register-condition")]
    [TestCase("opposite-branch")]
    [TestCase("different-merge")]
    [TestCase("argument-wrong-register")]
    [TestCase("argument-segment")]
    [TestCase("different-flag-store")]
    [TestCase("store-not-one")]
    [TestCase("wide-store")]
    [TestCase("different-literal")]
    [TestCase("wrong-return-register")]
    [TestCase("wide-literal-load")]
    [TestCase("flag-aliases-literal")]
    [TestCase("side-effect-after-load")]
    [TestCase("escaping-flags")]
    [TestCase("escaping-volatile-register")]
    [TestCase("bypassing-entry")]
    [TestCase("interior-entry")]
    [TestCase("indirect-branch")]
    [TestCase("extra-store")]
    [TestCase("branch-to-tail")]
    [TestCase("alternate-return")]
    [TestCase("epilogue-jump-to-tail")]
    public void NearMissesKeepInitializationExplicit(string defect)
    {
        var body = LiteralBody();
        void Change(int index, Func<Instruction, Instruction> change) => body[index] = change(body[index]);
        void Replace(int index, string hex)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(hex)), body[index].IP);
            body[index] = decoder.Decode();
        }
        switch (defect)
        {
            case "condition-not-zero": Change(1, i => { i.Immediate8 = 1; return i; }); break;
            case "wide-condition": Change(1, i => { i.Code = Code.Cmp_rm16_imm8; return i; }); break;
            case "register-condition": Replace(1, "80F900"); break;
            case "opposite-branch": Change(2, i => { i.Code = Code.Je_rel8_64; return i; }); break;
            case "different-merge": Change(2, i => { i.NearBranch64 = body[7].IP; return i; }); break;
            case "argument-wrong-register": Change(3, i => { i.Op0Register = Register.RDX; return i; }); break;
            case "argument-segment": Change(3, i => { i.SegmentPrefix = Register.GS; return i; }); break;
            case "different-flag-store": Change(5, i => { i.MemoryDisplacement64 = Flag + 1; return i; }); break;
            case "store-not-one": Change(5, i => { i.Immediate8 = 0; return i; }); break;
            case "wide-store": Change(5, i => { i.Code = Code.Mov_rm16_imm16; return i; }); break;
            case "different-literal": Change(6, i => { i.MemoryDisplacement64 = Slot + 8; return i; }); break;
            case "wrong-return-register": Change(6, i => { i.Op0Register = Register.RDX; return i; }); break;
            case "wide-literal-load": Change(6, i => { i.Code = Code.Mov_r32_rm32; return i; }); break;
            case "flag-aliases-literal":
                Change(1, i => { i.MemoryDisplacement64 = Slot; return i; });
                Change(5, i => { i.MemoryDisplacement64 = Slot; return i; });
                break;
            case "side-effect-after-load": Replace(7, "E800000000"); break;
            case "escaping-flags": Replace(7, "0F94C1"); break;
            case "escaping-volatile-register": Replace(7, "4889D1"); break;
            case "bypassing-entry":
            case "interior-entry":
                Replace(0, "E900000000");
                Change(0, i => { i.NearBranch64 = body[defect == "interior-entry" ? 4 : 6].IP; return i; });
                break;
            case "indirect-branch": Replace(8, "FFE1"); break;
            case "extra-store": body.Insert(5, body[5]); break;
            case "branch-to-tail":
                AppendUnreachableCode(body);
                Change(2, i => { i.NearBranch64 = body[9].IP; return i; });
                break;
            case "alternate-return":
                Replace(0, "7500");
                Change(0, i => { i.NearBranch64 = body[8].IP; return i; });
                break;
            case "epilogue-jump-to-tail":
                AppendUnreachableCode(body);
                Replace(7, "E900000000");
                Change(7, i => { i.NearBranch64 = body[9].IP; return i; });
                break;
        }
        var helpers = defect == "unknown-helper" ? new HashSet<ulong>() : new HashSet<ulong> { Helper };
        Assert.That(X86MetadataGuardProof.Find(body, helpers, address => defect != "not-literal" && address == Slot), Is.Null);
    }

    private static void AppendUnreachableCode(List<Instruction> body)
    {
        byte[] bytes = [0xCC, 0xCC, 0xFF, 0xE1, 0x90, 0xC3]; // padding; indirect jump; adjacent return
        var end = body[^1].NextIP + (ulong)bytes.Length;
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), body[^1].NextIP);
        while (decoder.IP < end)
            body.Add(decoder.Decode());
    }

    private static List<Instruction> LiteralBody()
    {
        var bytes = new List<byte>();
        void Emit(params byte[] data) => bytes.AddRange(data);
        void Rip(byte[] prefix, ulong target, params byte[] suffix)
        {
            var next = Base + (ulong)(bytes.Count + prefix.Length + 4 + suffix.Length);
            Emit(prefix);
            Emit(BitConverter.GetBytes(unchecked((int)(target - next))));
            Emit(suffix);
        }
        Emit(0x48, 0x83, 0xEC, 0x28); // sub rsp,40
        Rip([0x80, 0x3D], Flag, 0); // cmp byte[flag],0
        Emit(0x75, 19); // jne materialization, skipping LEA/CALL/store
        Rip([0x48, 0x8D, 0x0D], Slot); // lea rcx,[literalSlot]
        Rip([0xE8], Helper); // direct helper call
        Rip([0xC6, 0x05], Flag, 1); // mov byte[flag],1
        Rip([0x48, 0x8B, 0x05], Slot); // mov rax,[literalSlot]
        Emit(0x48, 0x83, 0xC4, 0x28, 0xC3); // add rsp,40; ret
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes.ToArray()), Base);
        var body = new List<Instruction>();
        while (decoder.IP < Base + (ulong)bytes.Count)
            body.Add(decoder.Decode());
        return body;
    }
}
