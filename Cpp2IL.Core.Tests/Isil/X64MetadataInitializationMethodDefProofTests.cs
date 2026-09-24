using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64MetadataInitializationMethodDefProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerHasMethodDefRouteAndUnrelatedEntryDoesNot()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_THROW_ONLY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_THROW_ONLY_FIXTURE_INPUT to the neutral synthetic player-input directory.");
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
            var owner = app.GetAssemblyByName("ThrowOnlyFixture")!.Types
                .Single(type => type.Name == "ThrowOnlyCases");
            var method = owner.Methods.Single(candidate => candidate.Name == "ThrowNotSupported");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;

            Assert.Multiple(() =>
            {
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyMethodDefArm(
                    app, pe, unwind, native[3].NearBranchTarget), Is.True);
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyMethodDefArm(
                    app, pe, unwind, native[11].NearBranchTarget), Is.False,
                    "the exception constructor is not the metadata initializer");
            });
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void MethodDefArmRequiresTokenTransferAndExactJoin()
    {
        const ulong resolver = 0x2200;
        const ulong join = 0x3300;
        var arm = new[]
        {
            Instruction.Create(Code.Mov_r32_rm32, Register.ECX, Register.R9D),
            Instruction.CreateBranch(Code.Call_rel32_64, resolver),
            Instruction.Create(Code.Mov_r64_rm64, Register.RBX, Register.RAX),
            Instruction.CreateBranch(Code.Jmp_rel32_64, join),
        };
        Assert.That(X64MetadataInitializationHelperProof.ProveMethodDefArmShape(
            arm, join, out var provenResolver), Is.True);
        Assert.That(provenResolver, Is.EqualTo(resolver));

        var wrongToken = arm.ToArray();
        wrongToken[0] = Instruction.Create(Code.Mov_r32_rm32, Register.ECX, Register.R8D);
        Assert.That(X64MetadataInitializationHelperProof.ProveMethodDefArmShape(
            wrongToken, join, out _), Is.False);

        var wrongResult = arm.ToArray();
        wrongResult[2] = Instruction.Create(Code.Mov_r64_rm64, Register.RBX, Register.RCX);
        Assert.That(X64MetadataInitializationHelperProof.ProveMethodDefArmShape(
            wrongResult, join, out _), Is.False);

        Assert.That(X64MetadataInitializationHelperProof.ProveMethodDefArmShape(
            arm, join + 1, out _), Is.False);
    }
}
