using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player control for the closed nested Boolean assignment.</summary>
[NonParallelizable]
public class X64NestedBooleanLiteralStoreFixtureTests
{
    [Test]
    public void PrivateAutoPropertyStoreRequiresItsProvedPublicSetter()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_NESTED_BOOLEAN_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NESTED_BOOLEAN_STORE_FIXTURE_INPUT to the neutral exact player input.");
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
            var types = app.GetAssemblyByName("NestedBooleanStoreFixture")!.Types;
            var owner = types.Single(type => type.Name == "BooleanOwner");
            var target = types.Single(type => type.Name == "BooleanTarget");
            var clearField = owner.Methods.Single(method => method.Name == "ClearFlag");
            var clearProperty = owner.Methods.Single(method => method.Name == "ClearState");
            var setter = target.Methods.Single(method => method.Name == "set_State");
            var direct = X64NestedBooleanLiteralStoreProof.Find(clearField);
            var property = X64NestedBooleanLiteralStoreProof.Find(clearProperty);
            Assert.Multiple(() =>
            {
                Assert.That(direct?.ValueSetter, Is.Null);
                Assert.That(property?.ValueSetter, Is.SameAs(setter));
                Assert.That(property?.ValueField.Name,
                    Is.EqualTo("<State>k__BackingField"));
                Assert.That(property?.Value, Is.False);
            });

            var originalName = setter.OverrideName;
            try
            {
                setter.OverrideName = "ChangedSetter";
                Assert.That(X64NestedBooleanLiteralStoreProof.Find(clearProperty),
                    Is.Null);
            }
            finally { setter.OverrideName = originalName; }

            var field = property!.ValueField;
            var attributes = field.Attributes;
            try
            {
                field.Attributes = (attributes & ~FieldAttributes.FieldAccessMask) |
                    FieldAttributes.Public;
                Assert.That(X64NestedBooleanLiteralStoreProof.Find(clearProperty),
                    Is.Null);
            }
            finally { field.Attributes = attributes; }

            var bytes = File.ReadAllBytes(binary);
            var offset = checked((int)((PE)app.Binary).MapVirtualAddressToRaw(
                setter.UnderlyingPointer, false));
            bytes[offset] ^= 1;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(bytes, File.ReadAllBytes(metadata),
                UnityVersion.Parse("2021.3.35f1"));
            var changed = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("NestedBooleanStoreFixture")!.Types
                .Single(type => type.Name == "BooleanOwner").Methods
                .Single(method => method.Name == "ClearState");
            Assert.That(X64NestedBooleanLiteralStoreProof.Find(changed), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

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
