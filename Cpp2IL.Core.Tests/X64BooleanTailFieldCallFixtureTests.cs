using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player check for a Boolean tail call through a reference field.</summary>
[NonParallelizable]
public class X64BooleanTailFieldCallFixtureTests
{
    [Test]
    public void BooleanTargetAndExclusiveNullArmAreBoundToPlayer()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_BOOLEAN_TAIL_FIELD_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BOOLEAN_TAIL_FIELD_CALL_FIXTURE_INPUT to the synthetic player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("BooleanTailFieldCallFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "BoolOwner");
            var receiver = assembly.Types.Single(type => type.Name == "BoolReceiver");
            var twin = assembly.Types.Single(type => type.Name == "BoolReceiverTwin");
            var method = owner.Methods.Single(candidate => candidate.Name == "ForwardRead");
            var target = receiver.Methods.Single(candidate => candidate.Name == "ReadEnabled");
            var twinTarget = twin.Methods.Single(candidate => candidate.Name == "ReadEnabled");
            Assert.That(method.Definition!.RawReturnType!.Type,
                Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN));
            Assert.That(target.Definition!.RawReturnType!.Type,
                Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN));
            target.EnsureRawBytes();
            var getterNative = X86Utils.Iterate(target).ToArray();
            Assert.That(X86DirectBooleanFieldGetterProof.Find(target, getterNative), Is.Not.Null,
                $"Boolean callee body must be separately proved: {string.Join(" | ", getterNative.Select(instruction => instruction.ToString()))}");
            twinTarget.EnsureRawBytes();
            Assert.That(X86DirectBooleanFieldGetterProof.Find(twinTarget,
                X86Utils.Iterate(twinTarget).ToArray()), Is.Not.Null);

            var proof = X64GuardedFieldCallProof.Find(method);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Target, Is.SameAs(target));
            Assert.That(proof.ReceiverField.Name, Is.EqualTo("Receiver"));
            Assert.That(proof.ArgumentFields, Is.Empty);
            Assert.That(twinTarget, Is.Not.SameAs(target));

            var bindings = app.MethodsByAddress[target.UnderlyingPointer];
            bindings.Add(target);
            try
            {
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "two matching Boolean target identities are ambiguous");
            }
            finally { bindings.RemoveAt(bindings.Count - 1); }

            try
            {
                target.OverrideReturnType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "a changed Boolean return signature cannot prove the ABI");
            }
            finally { target.OverrideReturnType = null; }

            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(native, Has.Length.EqualTo(8));
            var pe = (PE)app.Binary;
            var branchTail = checked((int)pe.MapVirtualAddressToRaw(
                native[3].NextIP - 1, false));
            Assert.That(getterNative[2].Code, Is.EqualTo(Iced.Intel.Code.Int3));
            var getterPadding = checked((int)pe.MapVirtualAddressToRaw(
                getterNative[2].IP, false));
            var changedGetter = File.ReadAllBytes(binary);
            changedGetter[getterPadding] = 0x90;
            var metadataBytes = File.ReadAllBytes(metadata);
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedGetter, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var changedGetterMethod = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("BooleanTailFieldCallFixture")!.Types
                .Single(type => type.Name == "BoolReceiver").Methods
                .Single(candidate => candidate.Name == "ReadEnabled");
            changedGetterMethod.EnsureRawBytes();
            Assert.That(X86DirectBooleanFieldGetterProof.Find(changedGetterMethod,
                X86Utils.Iterate(changedGetterMethod).ToArray()), Is.Null,
                "the unrelated native suffix must begin after genuine INT3 padding");

            var changedBranch = File.ReadAllBytes(binary);
            changedBranch[branchTail] ^= 1;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBranch, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var changedMethod = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("BooleanTailFieldCallFixture")!.Types
                .Single(type => type.Name == "BoolOwner").Methods
                .Single(candidate => candidate.Name == "ForwardRead");
            Assert.That(X64GuardedFieldCallProof.Find(changedMethod), Is.Null,
                "a retargeted null branch cannot prove the exclusive helper arm");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
