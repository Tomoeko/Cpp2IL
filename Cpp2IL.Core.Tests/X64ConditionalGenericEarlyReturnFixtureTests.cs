using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Exact-player controls for the separate guarded early-return leaves.</summary>
[NonParallelizable]
public class X64ConditionalGenericEarlyReturnFixtureTests
{
    [Test]
    public void NullAndDisabledExitsPreserveOrderedStoresAndReceiverIdentity()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CONDITIONAL_GENERIC_TERMINAL_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CONDITIONAL_GENERIC_TERMINAL_STORE_FIXTURE_INPUT to the neutral exact player input.");
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
                "ConditionalGenericTerminalStoreFixture")!.Types.Single(
                type => type.Name == "ConditionalGenericStores");
            var flag = owner.Methods.Single(method =>
                method.Name == "WriteFlagEarly");
            var pair = owner.Methods.Single(method =>
                method.Name == "WritePairEarly");
            var flagNative = X86Utils.Iterate(flag).ToArray();
            var pairNative = X86Utils.Iterate(pair).ToArray();

            var flagEvidence = X64ConditionalGenericBooleanStoreProof.Find(flag);
            var pairEvidence = X64ConditionalGenericBooleanStoreProof.Find(pair);
            Assert.Multiple(() =>
            {
                Assert.That(flagEvidence, Is.Not.Null);
                Assert.That(pairEvidence, Is.Not.Null);
                Assert.That(flagNative, Has.Length.EqualTo(9));
                Assert.That(pairNative, Has.Length.EqualTo(10));
                Assert.That(X64ConditionalGenericBooleanStoreProof
                    .TryProveShape(flagNative)?.IntegerOffset, Is.Null);
                Assert.That(X64ConditionalGenericBooleanStoreProof
                    .TryProveShape(pairNative)?.IntegerOffset, Is.Not.Null);
                Assert.That(pairEvidence?.Condition.Name, Is.EqualTo("Enabled"));
                Assert.That(pairEvidence?.BooleanTarget.Name,
                    Is.EqualTo("ResultFlag"));
                Assert.That(pairEvidence?.IntegerTarget?.Name,
                    Is.EqualTo("Counter"));
                Assert.That(pairEvidence?.AggregateValue?.Name,
                    Is.EqualTo("Value"));
                Assert.That(pairNative[1].NearBranchTarget,
                    Is.EqualTo(pairNative[^2].IP), "null must zero the return");
                Assert.That(pairNative[4].NearBranchTarget,
                    Is.EqualTo(pairNative[^1].IP),
                    "disabled must retain the receiver return");
                Assert.That(pairNative[5].IP, Is.LessThan(pairNative[6].IP),
                    "the Int32 store precedes the Boolean store");
            });

            var nullToReceiverReturn = pairNative.ToArray();
            nullToReceiverReturn[1].NearBranch64 = pairNative[^1].IP;
            Assert.That(X64ConditionalGenericBooleanStoreProof.TryProveShape(
                nullToReceiverReturn), Is.Null);
            var disabledToNullReturn = pairNative.ToArray();
            disabledToNullReturn[4].NearBranch64 = pairNative[^2].IP;
            Assert.That(X64ConditionalGenericBooleanStoreProof.TryProveShape(
                disabledToNullReturn), Is.Null);
            var disabledToStore = pairNative.ToArray();
            disabledToStore[4].NearBranch64 = pairNative[5].IP;
            Assert.That(X64ConditionalGenericBooleanStoreProof.TryProveShape(
                disabledToStore), Is.Null);

            var wrongReceiverCopy = pairNative.ToArray();
            wrongReceiverCopy[3].Op1Register = Register.RDX;
            Assert.That(X64ConditionalGenericBooleanStoreProof.TryProveShape(
                wrongReceiverCopy), Is.Null);
            var wrongNullReturn = pairNative.ToArray();
            wrongNullReturn[^2].Op1Register = Register.EDX;
            Assert.That(X64ConditionalGenericBooleanStoreProof.TryProveShape(
                wrongNullReturn), Is.Null);
            var wrongWriteOrder = pairNative.ToArray();
            wrongWriteOrder[5].Code = Code.Mov_rm8_r8;
            wrongWriteOrder[5].Op1Register = Register.R8L;
            wrongWriteOrder[5].MemoryDisplacement64 = pairNative[6].MemoryDisplacement64;
            wrongWriteOrder[6].Code = Code.Mov_rm32_r32;
            wrongWriteOrder[6].Op1Register = Register.EDX;
            wrongWriteOrder[6].MemoryDisplacement64 = pairNative[5].MemoryDisplacement64;
            Assert.That(X64ConditionalGenericBooleanStoreProof.TryProveShape(
                wrongWriteOrder), Is.Null);
            var wrongBooleanSource = pairNative.ToArray();
            wrongBooleanSource[6].Op1Register = Register.DL;
            Assert.That(X64ConditionalGenericBooleanStoreProof.TryProveShape(
                wrongBooleanSource), Is.Null);

            var rawReturn = pair.Definition!.RawReturnType!;
            var rawKind = rawReturn.Type;
            try
            {
                rawReturn.Type = Il2CppTypeEnum.IL2CPP_TYPE_OBJECT;
                Assert.That(X64ConditionalGenericBooleanStoreProof.Find(pair),
                    Is.Null, "the base return is bound to the original descriptor");
            }
            finally { rawReturn.Type = rawKind; }

            var changedBytes = File.ReadAllBytes(binary);
            var branchTail = checked((int)((PE)app.Binary)
                .MapVirtualAddressToRaw(pairNative[1].NextIP - 1, false));
            changedBytes[branchTail] ^= 1;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBytes,
                File.ReadAllBytes(metadata), UnityVersion.Parse("2021.3.35f1"));
            var changed = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ConditionalGenericTerminalStoreFixture")!
                .Types.Single(type => type.Name == "ConditionalGenericStores")
                .Methods.Single(method => method.Name == "WritePairEarly");
            Assert.That(X64ConditionalGenericBooleanStoreProof.Find(changed),
                Is.Null, "a changed original null branch cannot authorize recovery");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
