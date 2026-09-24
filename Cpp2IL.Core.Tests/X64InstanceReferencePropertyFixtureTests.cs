using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player control for a read/write reference property.</summary>
[NonParallelizable]
public class X64InstanceReferencePropertyFixtureTests
{
    [Test]
    public void GetterPresenceDoesNotInvalidateFoldedSetterStore()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_INSTANCE_REFERENCE_PROPERTY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_INSTANCE_REFERENCE_PROPERTY_FIXTURE_INPUT to the synthetic player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var assembly = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("InstanceReferencePropertyFixture")!;
            var reference = assembly.Types.Single(type =>
                type.FullName == "InstanceReferencePropertyFixture.ReferenceCell");
            var text = assembly.Types.Single(type =>
                type.FullName == "InstanceReferencePropertyFixture.TextCell");
            var referenceProperty = reference.Properties.Single(property => property.Name == "Value");
            var textProperty = text.Properties.Single(property => property.Name == "Value");
            Assert.That(referenceProperty.Getter, Is.Not.Null);
            Assert.That(textProperty.Getter, Is.Null);
            Assert.That(referenceProperty.Setter, Is.Not.Null);
            Assert.That(textProperty.Setter, Is.Not.Null);
            Assert.That(referenceProperty.Setter!.UnderlyingPointer,
                Is.EqualTo(textProperty.Setter!.UnderlyingPointer));

            foreach (var setter in new[] { referenceProperty.Setter, textProperty.Setter })
            {
                var evidence = X64InstanceReferenceSetterProof.Find(setter!);
                Assert.That(evidence, Is.Not.Null);
                Assert.That(evidence!.Field.Name, Is.EqualTo("Stored"));
            }
            Assert.That(X64InstanceReferenceSetterProof.Find(referenceProperty.Getter!), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
