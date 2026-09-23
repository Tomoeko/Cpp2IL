using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player evidence for the closed guarded field-argument call.</summary>
[NonParallelizable]
public class X64GuardedFieldCallFixtureTests
{
    [Test]
    public void FieldReceiverAndArgumentMustMatchTheSameNativeCall()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ARRAY_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ARRAY_CALL_FIXTURE_INPUT to the synthetic ArrayCallFixture player-input directory.");
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
            var assembly = app.GetAssemblyByName("ArrayCallFixture")!;
            var forwarder = assembly.Types.Single(type =>
                type.FullName == "ArrayCallFixture.FieldForwarder");
            var method = forwarder.Methods.Single(candidate => candidate.Name == "ForwardField");
            var proof = X64GuardedFieldCallProof.Find(method);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Target.Name, Is.EqualTo("Echo"));
            Assert.That(proof.ReceiverField.Name, Is.EqualTo("Receiver"));
            Assert.That(proof.ArgumentFields.Select(field => field.Name), Is.EqualTo(new[] { "Values" }));
            var other = assembly.Types.Single(type => type.FullName == "ArrayCallFixture.ArrayCalls")
                .Methods.Single(candidate => candidate.Name == "Forward");
            Assert.That(X64GuardedFieldCallProof.Find(other), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
