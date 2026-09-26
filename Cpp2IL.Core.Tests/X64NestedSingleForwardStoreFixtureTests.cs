using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using LibCpp2IL.PE;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64NestedSingleForwardStoreFixtureTests
{
    [Test]
    public void CompleteCallerAndLeafSetterAreRequired()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FLOAT_FORWARD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FLOAT_FORWARD_FIXTURE_INPUT to the neutral exact player input.");
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
            var types = app.GetAssemblyByName("FloatForwardStoreFixture")!.Types;
            var owner = types.Single(type => type.Name == "FloatOwner");
            var target = types.Single(type => type.Name == "FloatTarget");
            var forward = owner.Methods.Single(method => method.Name == "Forward");
            var setter = target.Methods.Single(method => method.Name == "SetLevel");
            var field = target.Fields.Single(candidate => candidate.Name == "_level");
            var evidence = X64NestedSingleForwardStoreProof.Find(forward);
            Assert.Multiple(() =>
            {
                Assert.That(evidence?.Setter, Is.SameAs(setter));
                Assert.That(evidence?.ValueField, Is.SameAs(field));
                Assert.That(evidence?.ReceiverField.Name, Is.EqualTo("Target"));
            });
            var targetLocal = new LocalVariable("target",
                new ManagedRegister(null, "target"), target);
            var fieldReference = new FieldReference(field, targetLocal,
                checked((int)field.Offset));
            Assert.Multiple(() =>
            {
                Assert.That(NarrowFieldEqualityProof.HasUnchangedSingleFieldLayout(
                    fieldReference), Is.True);
                Assert.That(NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                    fieldReference, 32), Is.False);
            });

            var originalAttributes = field.Attributes;
            try
            {
                field.Attributes = (originalAttributes & ~FieldAttributes.FieldAccessMask) |
                    FieldAttributes.Public;
                Assert.That(X64NestedSingleForwardStoreProof.Find(forward), Is.Null);
            }
            finally { field.Attributes = originalAttributes; }

            var originalName = setter.OverrideName;
            try
            {
                setter.OverrideName = "ChangedSetter";
                Assert.That(X64NestedSingleForwardStoreProof.Find(forward), Is.Null);
            }
            finally { setter.OverrideName = originalName; }

            var originalBytes = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            var pe = (PE)app.Binary;
            var mutationOffsets = new[]
            {
                checked((int)pe.MapVirtualAddressToRaw(forward.UnderlyingPointer, false)) + 11,
                checked((int)pe.MapVirtualAddressToRaw(setter.UnderlyingPointer, false)),
            };
            foreach (var offset in mutationOffsets)
            {
                var changed = (byte[])originalBytes.Clone();
                changed[offset] ^= 1;
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes,
                    UnityVersion.Parse("2021.3.35f1"));
                var altered = Cpp2IlApi.CurrentAppContext!
                    .GetAssemblyByName("FloatForwardStoreFixture")!.Types
                    .Single(type => type.Name == "FloatOwner").Methods
                    .Single(candidate => candidate.Name == "Forward");
                Assert.That(X64NestedSingleForwardStoreProof.Find(altered), Is.Null);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
