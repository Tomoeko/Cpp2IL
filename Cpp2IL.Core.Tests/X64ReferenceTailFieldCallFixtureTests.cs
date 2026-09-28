using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player checks for a class-return field tail call.</summary>
[NonParallelizable]
public class X64ReferenceTailFieldCallFixtureTests
{
    [Test]
    public void ClassReturnRequiresOneBoundTargetAndTheCompleteNullGuard()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_REFERENCE_TAIL_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_REFERENCE_TAIL_CALL_FIXTURE_INPUT to the synthetic player-input directory.");
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
            var owner = app.GetAssemblyByName("ReferenceTailCallFixture")!.Types
                .Single(type => type.Name == "BufferOwner");
            var method = owner.Methods.Single(candidate => candidate.Name == "ClearBuffer");
            Assert.That(method.Definition!.RawReturnType!.Type,
                Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_CLASS));
            method.EnsureRawBytes();
            Assert.That(method.RawBytes.Length, Is.EqualTo(29));
            Assert.That(X86Utils.Iterate(method).Count(), Is.EqualTo(8));

            var proof = X64GuardedFieldCallProof.Find(method);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.ReceiverField.Name, Is.EqualTo("Buffer"));
            Assert.That(proof.ArgumentFields, Is.Empty);
            Assert.That(proof.Target.Name, Is.EqualTo("Clear"));
            Assert.That(proof.Target.DeclaringType,
                Is.SameAs(proof.ReceiverField.FieldType));
            Assert.That(proof.Target.ReturnType, Is.SameAs(method.ReturnType));

            var bindings = app.MethodsByAddress[proof.Target.UnderlyingPointer];
            bindings.Add(proof.Target);
            try
            {
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "a second applicable call-target identity remains ambiguous");
            }
            finally { bindings.RemoveAt(bindings.Count - 1); }

            try
            {
                proof.ReceiverField.OverrideFieldType = owner;
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "an altered receiver-field type cannot identify the target");
            }
            finally { proof.ReceiverField.OverrideFieldType = null; }

            var native = X86Utils.Iterate(method).ToArray();
            var pe = (PE)app.Binary;
            var branchTail = checked((int)pe.MapVirtualAddressToRaw(
                native[3].NextIP - 1, false));
            var changedBinary = File.ReadAllBytes(binary);
            changedBinary[branchTail] ^= 1;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBinary, File.ReadAllBytes(metadata),
                UnityVersion.Parse("2021.3.35f1"));
            var changedMethod = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ReferenceTailCallFixture")!.Types
                .Single(type => type.Name == "BufferOwner").Methods
                .Single(candidate => candidate.Name == "ClearBuffer");
            Assert.That(X64GuardedFieldCallProof.Find(changedMethod), Is.Null,
                "a retargeted native null branch is not the proved tail call");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void VolatileFieldWithErasedModifierRemainsUnproved()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_REFERENCE_TAIL_CALL_VOLATILE_NEGATIVE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_REFERENCE_TAIL_CALL_VOLATILE_NEGATIVE_INPUT to the synthetic volatile-control player-input directory.");
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
            var owner = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ZeroArgFieldCallFixture")!.Types
                .Single(type => type.Name == "BclVolatileTailOwner");
            var field = owner.Fields.Single(candidate => candidate.Name == "Buffer");
            var method = owner.Methods.Single(candidate => candidate.Name == "ClearBufferVolatile");
            Assert.That(field.BackingData!.Field.RawFieldType!.NumMods, Is.Zero,
                "the player metadata alone does not retain the source volatile modifier");
            method.EnsureRawBytes();
            Assert.That(method.RawBytes.Length, Is.Not.EqualTo(29));
            Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
