using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player control for an unused reference argument in a nested store.</summary>
[NonParallelizable]
public class X64UnusedReferenceNestedStoreFixtureTests
{
    [Test]
    public void NativeStoreIgnoresOnlyAnUnchangedClassParameter()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_UNUSED_REFERENCE_NESTED_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_UNUSED_REFERENCE_NESTED_STORE_FIXTURE_INPUT to the synthetic player-input directory.");
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
            var assembly = app.GetAssemblyByName("NestedFlagSetterFixture")!;
            var owner = assembly.Types.Single(type =>
                type.FullName == "NestedFlagSetterFixture.FlagOwner");
            var method = owner.Methods.Single(candidate => candidate.Name == "SetNestedFlag");
            var parameter = method.Parameters.Single();
            var evidence = X64NestedBooleanLiteralStoreProof.Find(method);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.ReceiverField.Name, Is.EqualTo("Child"));
            Assert.That(evidence.ValueField.Name, Is.EqualTo("Flag"));
            Assert.That(evidence.Value, Is.True);

            try
            {
                parameter.OverrideParameterType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64NestedBooleanLiteralStoreProof.Find(method), Is.Null);
                parameter.OverrideParameterType = null;

                parameter.OverrideAttributes = ParameterAttributes.Out;
                Assert.That(X64NestedBooleanLiteralStoreProof.Find(method), Is.Null);
            }
            finally
            {
                parameter.OverrideParameterType = null;
                parameter.OverrideAttributes = null;
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
