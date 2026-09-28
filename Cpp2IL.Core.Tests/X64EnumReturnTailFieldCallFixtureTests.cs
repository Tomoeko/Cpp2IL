using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player checks for an Int32-backed enum tail return.</summary>
[NonParallelizable]
public class X64EnumReturnTailFieldCallFixtureTests
{
    [Test]
    public void PropertyGetterNeedsOneBoundEnumTargetAndTheCompleteNullArm()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_ENUM_RETURN_TAIL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ENUM_RETURN_TAIL_FIXTURE_INPUT to the synthetic player-input directory.");
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
            var assembly = app.GetAssemblyByName("EnumReturnTailFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "ChoiceOwner");
            var receiver = assembly.Types.Single(type => type.Name == "ChoiceReader");
            var choice = assembly.Types.Single(type => type.Name == "Choice");
            var method = owner.Methods.Single(candidate => candidate.Name == "get_CurrentChoice");
            var target = receiver.Methods.Single(candidate => candidate.Name == "ReadChoice");
            Assert.That(method.Definition!.RawReturnType!.Type,
                Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE));
            Assert.That(choice.IsEnumType, Is.True);
            Assert.That(choice.EnumUnderlyingType, Is.SameAs(app.SystemTypes.SystemInt32Type));
            Assert.That(choice.Fields.Single(field => !field.IsStatic).Name, Is.EqualTo("value__"));
            method.EnsureRawBytes();
            Assert.That(method.RawBytes.Length, Is.EqualTo(32));
            Assert.That(X86Utils.Iterate(method).Count(), Is.EqualTo(8));

            var proof = X64GuardedFieldCallProof.Find(method);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Target, Is.SameAs(target));
            Assert.That(proof.ReceiverField.Name, Is.EqualTo("Receiver"));
            Assert.That(proof.ReceiverField.Offset, Is.EqualTo(160));
            Assert.That(proof.ArgumentFields, Is.Empty);

            var bindings = app.MethodsByAddress[target.UnderlyingPointer];
            bindings.Add(target);
            try
            {
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "a second matching target identity remains ambiguous");
            }
            finally { bindings.RemoveAt(bindings.Count - 1); }

            try
            {
                choice.OverrideEnumUnderlyingType = app.SystemTypes.SystemUInt64Type;
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "a changed enum width cannot retain the proved return ABI");
                choice.OverrideEnumUnderlyingType = app.SystemTypes.SystemUInt32Type;
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "a changed enum signedness cannot retain the proved return ABI");
            }
            finally { choice.OverrideEnumUnderlyingType = null; }

            try
            {
                proof.ReceiverField.OverrideFieldType = owner;
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "an altered field type cannot select the tail target");
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
                .GetAssemblyByName("EnumReturnTailFixture")!.Types
                .Single(type => type.Name == "ChoiceOwner").Methods
                .Single(candidate => candidate.Name == "get_CurrentChoice");
            Assert.That(X64GuardedFieldCallProof.Find(changedMethod), Is.Null,
                "a retargeted native null branch is not the proved tail call");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void EqualBoundedShapeReturningAnOrdinaryStructStaysUnproved()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_ENUM_RETURN_TAIL_SHAPE_CONTROL_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ENUM_RETURN_TAIL_SHAPE_CONTROL_INPUT to the synthetic shape-control player-input directory.");
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
            var assembly = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ZeroArgFieldCallFixture")!;
            var enumMethod = assembly.Types.Single(type => type.Name == "EnumReturnOwner")
                .Methods.Single(candidate => candidate.Name == "Forward");
            var structMethod = assembly.Types.Single(type => type.Name == "TinyValueOwner")
                .Methods.Single(candidate => candidate.Name == "ForwardValue");
            enumMethod.EnsureRawBytes();
            structMethod.EnsureRawBytes();
            Assert.That(enumMethod.RawBytes.Length, Is.EqualTo(32));
            Assert.That(structMethod.RawBytes.Length, Is.EqualTo(32));
            Assert.That(X86Utils.Iterate(enumMethod).Count(), Is.EqualTo(8));
            Assert.That(X86Utils.Iterate(structMethod).Count(), Is.EqualTo(8));
            Assert.That(enumMethod.UnderlyingPointer,
                Is.Not.EqualTo(structMethod.UnderlyingPointer),
                "the control must not be rejected solely because of a folded caller address");
            Assert.That(Cpp2IlApi.CurrentAppContext!.MethodsByAddress[enumMethod.UnderlyingPointer],
                Has.Count.EqualTo(1));
            Assert.That(Cpp2IlApi.CurrentAppContext.MethodsByAddress[structMethod.UnderlyingPointer],
                Has.Count.EqualTo(1));
            Assert.That(enumMethod.ReturnType.IsEnumType, Is.True);
            Assert.That(structMethod.ReturnType.IsEnumType, Is.False);
            Assert.That(X64GuardedFieldCallProof.Find(enumMethod), Is.Not.Null);
            Assert.That(X64GuardedFieldCallProof.Find(structMethod), Is.Null,
                "native return width alone cannot prove an enum declaration");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
