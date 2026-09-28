using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player control for the effect, getter and Boolean store.</summary>
[NonParallelizable]
public class X64CallResultBooleanLiteralStoreFixtureTests
{
    [Test]
    public void StoreRequiresOrderedCallsExactFieldsAndNullBranch()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CALL_RESULT_BOOLEAN_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CALL_RESULT_BOOLEAN_STORE_FIXTURE_INPUT to the neutral exact player input.");
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
            var assembly = app.GetAssemblyByName("CallResultBooleanStoreFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "FlagOwner");
            var receiver = assembly.Types.Single(type => type.Name == "FlagCell");
            var constructor = owner.Methods.Single(method => method.Name == ".ctor");
            var effect = owner.Methods.Single(method => method.Name == "Touch");
            var getter = owner.Methods.Single(method => method.Name == "GetCell");
            var stored = receiver.Fields.Single(field => field.Name == "Enabled");
            var ownerCell = owner.Fields.Single(field => field.Name == "Cell");
            var neighbor = receiver.Fields.Single(field => field.Name == "Neighbor");
            Assert.That(app.MethodsByAddress[getter.UnderlyingPointer].Count,
                Is.GreaterThan(1), "the exact-target getter must remain folded");
            Assert.Multiple(() =>
            {
                Assert.That(X64CallResultBooleanLiteralStoreProof.HasOrdinaryMethodIdentity(constructor),
                    Is.False, "constructor calls require a separate base-chain proof");
                Assert.That(X64CallResultBooleanLiteralStoreProof.HasOrdinaryMethodIdentity(effect),
                    Is.True);
            });

            foreach (var (name, expected) in new[] { ("Enable", true), ("Disable", false) })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                var native = X86Utils.Iterate(method).ToArray();
                var evidence = X64CallResultBooleanLiteralStoreProof.Find(method);
                Assert.Multiple(() =>
                {
                    Assert.That(native, Has.Length.EqualTo(15));
                    Assert.That(native[4].NearBranchTarget, Is.EqualTo(effect.UnderlyingPointer));
                    Assert.That(native[7].NearBranchTarget, Is.EqualTo(getter.UnderlyingPointer));
                    Assert.That(native[9].NearBranchTarget, Is.EqualTo(native[14].IP));
                    Assert.That(evidence?.Effect, Is.SameAs(effect));
                    Assert.That(evidence?.Getter, Is.SameAs(getter));
                    Assert.That(evidence?.Field, Is.SameAs(stored));
                    Assert.That(evidence?.Value, Is.EqualTo(expected));
                });

                var bindings = app.MethodsByAddress[getter.UnderlyingPointer];
                bindings.Add(getter);
                try
                {
                    Assert.That(X64CallResultBooleanLiteralStoreProof.Find(method), Is.Null,
                        "a second applicable folded getter identity is ambiguous");
                }
                finally { bindings.RemoveAt(bindings.Count - 1); }

                try
                {
                    stored.OverrideFieldType = app.SystemTypes.SystemByteType;
                    Assert.That(X64CallResultBooleanLiteralStoreProof.Find(method), Is.Null,
                        "a byte field is not the proved Boolean field");
                }
                finally { stored.OverrideFieldType = null; }

                try
                {
                    neighbor.OverrideOffset = stored.Offset;
                    Assert.That(X64CallResultBooleanLiteralStoreProof.Find(method), Is.Null,
                        "neighboring storage may not overlap the Boolean store");
                }
                finally { neighbor.OverrideOffset = null; }

                try
                {
                    ownerCell.OverrideOffset = ownerCell.DefaultOffset + 8;
                    Assert.That(X64CallResultBooleanLiteralStoreProof.Find(method), Is.Null,
                        "the getter must still read its exact reference field");
                }
                finally { ownerCell.OverrideOffset = null; }

                var originalName = effect.OverrideName;
                try
                {
                    effect.OverrideName = "ChangedEffect";
                    Assert.That(X64CallResultBooleanLiteralStoreProof.Find(method), Is.Null,
                        "the prior side-effect target must retain its identity");
                }
                finally { effect.OverrideName = originalName; }
            }

            var enable = owner.Methods.Single(method => method.Name == "Enable");
            var branch = X86Utils.Iterate(enable).ToArray()[9];
            var branchTail = checked((int)((PE)app.Binary).MapVirtualAddressToRaw(
                branch.NextIP - 1, false));
            var changedBinary = File.ReadAllBytes(binary);
            changedBinary[branchTail] ^= 1;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBinary, File.ReadAllBytes(metadata),
                UnityVersion.Parse("2021.3.35f1"));
            var changed = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("CallResultBooleanStoreFixture")!.Types
                .Single(type => type.Name == "FlagOwner").Methods
                .Single(method => method.Name == "Enable");
            Assert.That(X64CallResultBooleanLiteralStoreProof.Find(changed), Is.Null,
                "a retargeted branch cannot prove the exclusive null-helper edge");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
