using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;

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

    [Test]
    public void ClosedLiteralGuardPreservesOneScheduledRegisterMove()
    {
        var body = LiteralBody([0x48, 0x89, 0xCB]); // mov rbx,rcx
        var helpers = new HashSet<ulong> { Helper };
        var proof = X86MetadataGuardProof.Find(body, helpers, address => address == Slot);

        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.MaterializationAddress, Is.EqualTo(body[7].IP));
            Assert.That(proof.RemovedAddresses,
                Is.EqualTo(new[] { body[1].IP, body[3].IP, body[4].IP, body[5].IP, body[6].IP }));
            Assert.That(proof.RemovedAddresses, Does.Not.Contain(body[2].IP));
            Assert.That(X86MetadataGuardProof.FindUnresolvedInitializationGuards(body, helpers,
                address => address == Slot), Is.EquivalentTo(new[] { body[1].IP }));
        });
    }

    [TestCase("flag-writing move")]
    [TestCase("memory-reading move")]
    [TestCase("memory-writing move")]
    [TestCase("partial-register move")]
    [TestCase("two moves")]
    [TestCase("unknown slot")]
    public void MovedLiteralGuardRejectsUnprovedVariants(string defect)
    {
        var move = defect switch
        {
            "flag-writing move" => new byte[] { 0x48, 0x03, 0xD9 }, // add rbx,rcx
            "memory-reading move" => [0x48, 0x8B, 0x19], // mov rbx,[rcx]
            "memory-writing move" => [0x48, 0x89, 0x19], // mov [rcx],rbx
            "partial-register move" => [0x88, 0xCB], // mov bl,cl
            "two moves" => [0x48, 0x89, 0xCB, 0x48, 0x89, 0xF3],
            _ => [0x48, 0x89, 0xCB],
        };
        var body = LiteralBody(move);
        var helpers = new HashSet<ulong> { Helper };
        bool KnownSlot(ulong address) => defect != "unknown slot" && address == Slot;

        Assert.Multiple(() =>
        {
            Assert.That(X86MetadataGuardProof.Find(body, helpers, KnownSlot), Is.Null);
            Assert.That(X86MetadataGuardProof.FindUnresolvedInitializationGuards(body, helpers,
                KnownSlot), Is.Empty);
        });
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerClassifiesMovedTypeInfoGuardsAndRejectsMutations()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_METADATA_GUARD_MOVE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_METADATA_GUARD_MOVE_FIXTURE_INPUT to the neutral fixture's exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("MetadataGuardMoveFixture")!.Types
                .Single(type => type.Name == "SharedState");
            var keyFunctions = app.GetOrCreateKeyFunctionAddresses();
            var helpers = new HashSet<ulong> { keyFunctions.il2cpp_codegen_initialize_runtime_metadata,
                keyFunctions.il2cpp_codegen_initialize_runtime_metadata_inline };
            helpers.Remove(0);

            foreach (var name in new[] { "AddParameter", "AddBox" })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                method.EnsureRawBytes();
                var decoded = X86Utils.Iterate(method).ToArray();
                var firstReturn = Array.FindIndex(decoded, instruction => instruction.Mnemonic == Mnemonic.Ret);
                Assert.That(firstReturn, Is.GreaterThan(0), name);
                var body = decoded.Take(firstReturn + 1).ToList();
                var guards = X86MetadataGuardProof.FindUnresolvedInitializationGuards(method, body);
                Assert.That(guards, Has.Count.EqualTo(1), name);
                var index = body.FindIndex(instruction => guards.Contains(instruction.IP));
                Assert.That(index, Is.GreaterThanOrEqualTo(0), name);
                var move = body[index + 1];
                Assert.That(move.Mnemonic, Is.EqualTo(Mnemonic.Mov), name);
                Assert.That(move.Op0Kind, Is.EqualTo(OpKind.Register), name);
                Assert.That(move.Op1Kind, Is.EqualTo(OpKind.Register), name);
                Assert.That(move.Op0Register, Is.EqualTo(name == "AddBox" ? Register.RBX : Register.EBX), name);
                Assert.That(move.Op1Register, Is.EqualTo(name == "AddBox" ? Register.RCX : Register.ECX), name);

                var argument = body[index + 3];
                var call = body[index + 4];
                var store = body[index + 5];
                var slot = argument.IPRelativeMemoryAddress;
                var usage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(slot);
                Assert.That(usage, Is.Not.Null, name);
                Assert.That(usage!.Type, Is.EqualTo(LibCpp2IL.MetadataUsageType.TypeInfo), name);
                Assert.That(usage.IsValid, Is.True, name);
                Assert.That(app.ResolveIl2CppType(usage.AsType()), Is.SameAs(owner), name);
                Assert.That(helpers, Does.Contain(call.NearBranchTarget), name);
                Assert.That(store.IPRelativeMemoryAddress, Is.EqualTo(body[index].IPRelativeMemoryAddress), name);
                Assert.That(body[index + 2].NearBranchTarget, Is.EqualTo(body[index + 6].IP), name);
                Assert.That(X86MetadataGuardProof.Find(body, helpers,
                    address => app.LibCpp2IlContext.GetLiteralByAddress(address) != null), Is.Null,
                    "TypeInfo initialization must remain explicit");

                bool KnownSlot(ulong address) => address == slot;
                Assert.That(X86MetadataGuardProof.FindUnresolvedInitializationGuards(body, helpers,
                    KnownSlot), Is.EquivalentTo(guards), name);
                Assert.That(X86MetadataGuardProof.FindUnresolvedInitializationGuards(body, helpers,
                    _ => false), Is.Empty, "unproved slot");

                foreach (var defect in new[] { "flags", "memory", "partial", "branch", "helper", "flag store" })
                {
                    var changed = body.ToList();
                    var instructionIndex = defect switch
                    {
                        "branch" => index + 2,
                        "helper" => index + 4,
                        "flag store" => index + 5,
                        _ => index + 1,
                    };
                    var instruction = changed[instructionIndex];
                    switch (defect)
                    {
                        case "flags":
                            instruction.Code = name == "AddBox" ? Code.Add_rm64_r64 : Code.Add_rm32_r32;
                            break;
                        case "memory":
                            instruction.Op1Kind = OpKind.Memory;
                            instruction.MemoryBase = Register.RCX;
                            break;
                        case "partial":
                            instruction.Code = Code.Mov_r8_rm8;
                            instruction.Op0Register = Register.BL;
                            instruction.Op1Register = Register.CL;
                            break;
                        case "branch": instruction.NearBranch64 = body[index + 7].IP; break;
                        case "helper": instruction.NearBranch64++; break;
                        case "flag store": instruction.MemoryDisplacement64++; break;
                    }
                    changed[instructionIndex] = instruction;
                    Assert.That(X86MetadataGuardProof.FindUnresolvedInitializationGuards(changed, helpers,
                        KnownSlot), Is.Empty, name + " " + defect);
                }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void ExactNonliteralInitializationGuardIsReportedButNotRemoved()
    {
        var body = LiteralBody();
        var materialize = body[6];
        materialize.MemoryDisplacement64 = Slot + 8;
        body[6] = materialize;
        var helpers = new HashSet<ulong> { Helper };

        Assert.That(X86MetadataGuardProof.Find(body, helpers, address => address == Slot), Is.Null);
        Assert.That(X86MetadataGuardProof.FindUnresolvedInitializationGuards(body, helpers),
            Is.EquivalentTo(new[] { body[1].IP }));

        var wrongHelper = new HashSet<ulong>();
        Assert.That(X86MetadataGuardProof.FindUnresolvedInitializationGuards(body, wrongHelper), Is.Empty);
        var wrongStore = body[5];
        wrongStore.MemoryDisplacement64 = Flag + 1;
        body[5] = wrongStore;
        Assert.That(X86MetadataGuardProof.FindUnresolvedInitializationGuards(body, helpers), Is.Empty);
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

    private static List<Instruction> LiteralBody(byte[]? registerMove = null)
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
        if (registerMove != null)
            Emit(registerMove);
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
