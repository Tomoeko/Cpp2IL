using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player control for the closed nested Boolean assignment.</summary>
[NonParallelizable]
public class X64NestedBooleanLiteralStoreFixtureTests
{
    [Test]
    public void LiteralStoreRequiresMatchingNativeBodyAndUnchangedFieldMetadata()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FIELD_GUARD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FIELD_GUARD_FIXTURE_INPUT to the synthetic FieldGuardFixture player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var assembly = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("FieldGuardFixture")!;
            var owner = assembly.Types.Single(type => type.FullName == "FieldGuardFixture.NestedBooleanOwner");
            foreach (var (name, expected) in new[] { ("SetTrue", true), ("SetFalse", false) })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                var evidence = X64NestedBooleanLiteralStoreProof.Find(method);
                Assert.That(evidence, Is.Not.Null);
                Assert.That(evidence!.Value, Is.EqualTo(expected));
                Assert.That(evidence.ReceiverField.Name, Is.EqualTo("Inner"));
                Assert.That(evidence.ValueField.Name, Is.EqualTo("Value"));
                var attributes = evidence.ValueField.Attributes;
                try
                {
                    evidence.ValueField.Attributes |= FieldAttributes.Static;
                    Assert.That(X64NestedBooleanLiteralStoreProof.Find(method), Is.Null);
                    evidence.ValueField.Attributes = (attributes & ~FieldAttributes.FieldAccessMask) |
                        FieldAttributes.Private;
                    Assert.That(X64NestedBooleanLiteralStoreProof.Find(method), Is.Null);
                    evidence.ValueField.Attributes = attributes | FieldAttributes.InitOnly;
                    Assert.That(X64NestedBooleanLiteralStoreProof.Find(method), Is.Null);
                }
                finally { evidence.ValueField.Attributes = attributes; }
            }
            var integerStore = assembly.Types.Single(type => type.FullName == "FieldGuardFixture.NestedFieldBox")
                .Methods.Single(method => method.Name == "ClearInner");
            Assert.That(X64NestedBooleanLiteralStoreProof.Find(integerStore), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
