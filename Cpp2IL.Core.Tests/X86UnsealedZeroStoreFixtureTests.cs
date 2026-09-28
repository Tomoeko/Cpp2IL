using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X86UnsealedZeroStoreFixtureTests
{
    [Test]
    public void NestedZeroStoreNeedsBothUnchangedLayoutsAndTheExactNullBranch()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_UNSEALED_ZERO_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_UNSEALED_ZERO_STORE_FIXTURE_INPUT to the neutral player-input directory.");
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
            var assembly = app.GetAssemblyByName("UnsealedZeroStoreFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "StoreOwner");
            var box = assembly.Types.Single(type => type.Name == "StoreBox");
            var receiver = owner.Fields.Single(field => field.Name == "Box");
            var ownerNeighbor = owner.Fields.Single(field => field.Name == "Suffix");
            var stored = box.Fields.Single(field => field.Name == "Count");
            var boxNeighbor = box.Fields.Single(field => field.Name == "Neighbor");
            var method = owner.Methods.Single(candidate => candidate.Name == "Clear");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var shape = X86GuardedZeroStoreProof.TryProveShape(native);
            Assert.Multiple(() =>
            {
                Assert.That(owner.Attributes & TypeAttributes.Sealed, Is.EqualTo((TypeAttributes)0));
                Assert.That(box.Attributes & TypeAttributes.Sealed, Is.EqualTo((TypeAttributes)0));
                Assert.That(method.IsVirtual, Is.False);
                Assert.That(shape?.ReceiverOffset, Is.EqualTo(receiver.Offset));
                Assert.That(shape?.StoreOffset, Is.EqualTo(stored.Offset));
                Assert.That(shape?.StoreWidth, Is.EqualTo(4));
                Assert.That(native, Has.Length.EqualTo(8));
            });
            var proof = X86GuardedZeroStoreProof.Find(method, native);
            Assert.That(proof?.ReceiverField, Is.SameAs(receiver));
            Assert.That(proof?.Field, Is.SameAs(stored));

            try
            {
                ownerNeighbor.OverrideOffset = receiver.Offset;
                Assert.That(X86GuardedZeroStoreProof.Find(method, native), Is.Null,
                    "an overlapping owner field cannot prove the receiver layout");
            }
            finally { ownerNeighbor.OverrideOffset = null; }

            try
            {
                boxNeighbor.OverrideOffset = stored.Offset;
                Assert.That(X86GuardedZeroStoreProof.Find(method, native), Is.Null,
                    "an overlapping store field cannot prove the receiver layout");
            }
            finally { boxNeighbor.OverrideOffset = null; }

            try
            {
                receiver.OverrideFieldType = app.SystemTypes.SystemObjectType;
                Assert.That(X86GuardedZeroStoreProof.Find(method, native), Is.Null,
                    "a changed receiver identity cannot bind the native field load");
            }
            finally { receiver.OverrideFieldType = null; }

            var branchTail = checked((int)((PE)app.Binary).MapVirtualAddressToRaw(
                native[3].NextIP - 1, false));
            var changedBinary = File.ReadAllBytes(binary);
            changedBinary[branchTail] ^= 1;
            var metadataBytes = File.ReadAllBytes(metadata);
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBinary, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var changedMethod = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("UnsealedZeroStoreFixture")!.Types
                .Single(type => type.Name == "StoreOwner").Methods
                .Single(candidate => candidate.Name == "Clear");
            changedMethod.EnsureRawBytes();
            Assert.That(X86GuardedZeroStoreProof.Find(changedMethod,
                X86Utils.Iterate(changedMethod).ToArray()), Is.Null,
                "a retargeted native null branch cannot retain the proof");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
