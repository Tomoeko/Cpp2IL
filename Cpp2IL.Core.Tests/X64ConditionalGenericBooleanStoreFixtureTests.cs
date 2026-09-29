using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player control for the complete nullable store leaves.</summary>
[NonParallelizable]
public class X64ConditionalGenericBooleanStoreFixtureTests
{
    [Test]
    public void ConstructedReceiverRequiresOriginalLayoutAndOrderedNativeStores()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CONDITIONAL_GENERIC_BOOLEAN_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CONDITIONAL_GENERIC_BOOLEAN_STORE_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data",
            "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName(
                "ConditionalGenericBooleanStoreFixture")!.Types.Single(
                type => type.Name == "ConditionalGenericStores");
            var flag = owner.Methods.Single(method =>
                method.Name == "WriteFlagWhenEnabled");
            var pair = owner.Methods.Single(method =>
                method.Name == "WritePairWhenEnabled");
            var flagEvidence = X64ConditionalGenericBooleanStoreProof.Find(flag);
            var pairEvidence = X64ConditionalGenericBooleanStoreProof.Find(pair);
            Assert.Multiple(() =>
            {
                Assert.That(flagEvidence, Is.Not.Null);
                Assert.That(pairEvidence, Is.Not.Null);
                Assert.That(flagEvidence?.Condition.Name, Is.EqualTo("Enabled"));
                Assert.That(flagEvidence?.BooleanTarget.Name,
                    Is.EqualTo("ResultFlag"));
                Assert.That(flagEvidence?.IntegerTarget, Is.Null);
                Assert.That(pairEvidence?.IntegerTarget?.Name,
                    Is.EqualTo("Counter"));
                Assert.That(pairEvidence?.AggregateValue?.Name,
                    Is.EqualTo("Value"));
            });

            var flagNative = X86Utils.Iterate(flag).ToArray();
            var pairNative = X86Utils.Iterate(pair).ToArray();
            var flagShape = X64ConditionalGenericBooleanStoreProof
                .TryProveShape(flagNative);
            var pairShape = X64ConditionalGenericBooleanStoreProof
                .TryProveShape(pairNative);
            Assert.Multiple(() =>
            {
                Assert.That(flagNative, Has.Length.EqualTo(7));
                Assert.That(pairNative, Has.Length.EqualTo(8));
                Assert.That(flagShape?.IntegerOffset, Is.Null);
                Assert.That(pairShape?.IntegerOffset, Is.Not.Null);
                Assert.That(flagShape?.BooleanOffset,
                    Is.EqualTo(pairShape?.BooleanOffset));
                Assert.That(pairShape?.IntegerOffset,
                    Is.Not.EqualTo(pairShape?.BooleanOffset));
                Assert.That(pairNative[5].IP, Is.LessThan(pairNative[6].IP),
                    "the 32-bit write must precede the byte write");
                Assert.That(pairNative[2].NearBranchTarget,
                    Is.EqualTo(pairNative[^1].IP));
                Assert.That(pairNative[4].NearBranchTarget,
                    Is.EqualTo(pairNative[^1].IP));
            });

            var wrongOrder = pairNative.ToArray();
            wrongOrder[5].Code = Code.Mov_rm8_r8;
            wrongOrder[5].Op1Register = Register.R8L;
            wrongOrder[5].MemoryDisplacement64 = pairNative[6].MemoryDisplacement64;
            wrongOrder[6].Code = Code.Mov_rm32_r32;
            wrongOrder[6].Op1Register = Register.EDX;
            wrongOrder[6].MemoryDisplacement64 = pairNative[5].MemoryDisplacement64;
            Assert.That(X64ConditionalGenericBooleanStoreProof.TryProveShape(
                wrongOrder), Is.Null, "reversing the two writes changes effect order");

            var wrongIntegerSource = pairNative.ToArray();
            wrongIntegerSource[5].Op1Register = Register.R9D;
            Assert.That(X64ConditionalGenericBooleanStoreProof.TryProveShape(
                wrongIntegerSource), Is.Null);
            var wrongBooleanSource = pairNative.ToArray();
            wrongBooleanSource[6].Op1Register = Register.DL;
            Assert.That(X64ConditionalGenericBooleanStoreProof.TryProveShape(
                wrongBooleanSource), Is.Null);
            var splitExit = pairNative.ToArray();
            splitExit[4].NearBranch64 = pairNative[5].IP;
            Assert.That(X64ConditionalGenericBooleanStoreProof.TryProveShape(
                splitExit), Is.Null, "the disabled edge must not execute either store");

            var boolean = pairEvidence!.BooleanTarget;
            var attributes = boolean.Attributes;
            try
            {
                boolean.Attributes |= FieldAttributes.InitOnly;
                Assert.That(X64ConditionalGenericBooleanStoreProof.Find(pair),
                    Is.Null, "a nonconstructor cannot store to an initonly field");
            }
            finally { boolean.Attributes = attributes; }

            var condition = pairEvidence.Condition;
            try
            {
                condition.OverrideOffset = condition.DefaultOffset + 1;
                Assert.That(X64ConditionalGenericBooleanStoreProof.Find(pair),
                    Is.Null, "projected storage must retain its original field offset");
            }
            finally { condition.OverrideOffset = null; }

            var integer = pairEvidence.IntegerTarget!;
            try
            {
                integer.OverrideFieldType = app.SystemTypes.SystemBooleanType;
                Assert.That(X64ConditionalGenericBooleanStoreProof.Find(pair),
                    Is.Null, "the 32-bit native write cannot become Boolean");
            }
            finally { integer.OverrideFieldType = null; }

            var rawField = integer.BackingData!.Field.RawFieldType!;
            var rawKind = rawField.Type;
            try
            {
                rawField.Type =
                    LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN;
                Assert.That(X64ConditionalGenericBooleanStoreProof.Find(pair),
                    Is.Null, "the original field descriptor must remain I4");
            }
            finally { rawField.Type = rawKind; }

            var predecessor = pairEvidence.Receiver.GenericType.Fields.Single(
                field => field.Name == "Identity");
            var predecessorRaw = predecessor.BackingData!.Field.RawFieldType!;
            var predecessorKind = predecessorRaw.Type;
            try
            {
                predecessorRaw.Type =
                    LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE;
                Assert.That(X64ConditionalGenericBooleanStoreProof.Find(pair),
                    Is.Null,
                    "an unproved value-type neighbor cannot supply generic layout");
            }
            finally { predecessorRaw.Type = predecessorKind; }

            var returnType = pair.OverrideReturnType;
            try
            {
                pair.OverrideReturnType = app.SystemTypes.SystemObjectType;
                Assert.That(X64ConditionalGenericBooleanStoreProof.Find(pair),
                    Is.Null, "the returned base identity comes from the original signature");
            }
            finally { pair.OverrideReturnType = returnType; }

            var aggregate = pairEvidence.AggregateValue!.DeclaringType.Definition!;
            var sizePointer = app.Binary.TypeDefinitionSizePointers[
                aggregate.TypeIndex.Value];
            var sizeOffset = checked((int)((PE)app.Binary)
                .MapVirtualAddressToRaw(sizePointer, false));
            var enlargedBytes = File.ReadAllBytes(binary);
            BinaryPrimitives.WriteInt32LittleEndian(
                enlargedBytes.AsSpan(sizeOffset + 4, 4), 8);
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(enlargedBytes,
                File.ReadAllBytes(metadata), UnityVersion.Parse("2021.3.35f1"));
            var enlarged = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ConditionalGenericBooleanStoreFixture")!
                .Types.Single(type => type.Name == "ConditionalGenericStores")
                .Methods.Single(method => method.Name == "WritePairWhenEnabled");
            Assert.That(X64ConditionalGenericBooleanStoreProof.Find(enlarged),
                Is.Null, "the by-value register aggregate must remain four bytes");

            var changedBytes = File.ReadAllBytes(binary);
            var branchTail = checked((int)((PE)app.Binary).MapVirtualAddressToRaw(
                pairNative[2].NextIP - 1, false));
            changedBytes[branchTail] ^= 1;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBytes,
                File.ReadAllBytes(metadata), UnityVersion.Parse("2021.3.35f1"));
            var changed = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ConditionalGenericBooleanStoreFixture")!
                .Types.Single(type => type.Name == "ConditionalGenericStores")
                .Methods.Single(method =>
                    method.Name == "WritePairWhenEnabled");
            Assert.That(X64ConditionalGenericBooleanStoreProof.Find(changed),
                Is.Null, "a changed null-exit branch cannot authorize this recovery");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
