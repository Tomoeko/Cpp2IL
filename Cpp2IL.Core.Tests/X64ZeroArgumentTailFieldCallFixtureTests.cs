using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player check for a folded zero-argument tail target.</summary>
[NonParallelizable]
public class X64ZeroArgumentTailFieldCallFixtureTests
{
    [Test]
    public void FoldedTargetIsSelectedByTheProvedReceiverClass()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_ZERO_ARG_FIELD_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ZERO_ARG_FIELD_CALL_FIXTURE_INPUT to the synthetic player-input directory.");
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
            var assembly = app.GetAssemblyByName("ZeroArgFieldCallFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "CallOwner");
            var receiver = assembly.Types.Single(type => type.Name == "CallReceiver");
            var twin = assembly.Types.Single(type => type.Name == "CallReceiverTwin");
            var method = owner.Methods.Single(candidate => candidate.Name == "ForwardRead");
            var target = receiver.Methods.Single(candidate => candidate.Name == "ReadCalls");
            var twinTarget = twin.Methods.Single(candidate => candidate.Name == "ReadCalls");
            Assert.That(target.UnderlyingPointer, Is.EqualTo(twinTarget.UnderlyingPointer),
                "the exact-target control must retain its folded native target");
            Assert.That(app.MethodsByAddress[target.UnderlyingPointer].Count,
                Is.GreaterThan(1));

            var proof = X64GuardedFieldCallProof.Find(method);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Target, Is.SameAs(target));
            Assert.That(proof.ReceiverField.Name, Is.EqualTo("Receiver"));
            Assert.That(proof.ArgumentFields, Is.Empty);

            var bindings = app.MethodsByAddress[target.UnderlyingPointer];
            bindings.Add(target);
            try
            {
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "two applicable method identities must remain ambiguous");
            }
            finally { bindings.RemoveAt(bindings.Count - 1); }

            try
            {
                proof.ReceiverField.OverrideFieldType = twin;
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "an altered receiver-field type cannot select the folded target");
            }
            finally { proof.ReceiverField.OverrideFieldType = null; }

            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(native, Has.Length.EqualTo(8));
            var pe = (PE)app.Binary;
            var branchTail = checked((int)pe.MapVirtualAddressToRaw(
                native[3].NextIP - 1, false));
            var changedBinary = File.ReadAllBytes(binary);
            changedBinary[branchTail] ^= 1;
            var metadataBytes = File.ReadAllBytes(metadata);
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBinary, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var changedMethod = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ZeroArgFieldCallFixture")!.Types
                .Single(type => type.Name == "CallOwner").Methods
                .Single(candidate => candidate.Name == "ForwardRead");
            Assert.That(X64GuardedFieldCallProof.Find(changedMethod), Is.Null,
                "a retargeted native null branch is not the proved tail call");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
