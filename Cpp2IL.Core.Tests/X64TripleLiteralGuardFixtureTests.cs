using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64TripleLiteralGuardFixtureTests
{
    [Test]
    public void ThreeNullArmsAndLiteralChoiceRequireTheCompleteNativeBinding()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_TRIPLE_LITERAL_GUARD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_TRIPLE_LITERAL_GUARD_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data",
            "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        var image = File.ReadAllBytes(binary);
        var metadataBytes = File.ReadAllBytes(metadata);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(image, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var method = Compose();
            var evidence = X64TripleLiteralGuardProof.Find(method);
            Assert.That(evidence, Is.Not.Null);
            var app = method.AppContext;
            var text = app.SystemTypes.SystemStringType;
            var twoArgumentConcat = text.Methods.Single(candidate =>
                candidate.Name == "Concat" && candidate.Parameters.Count == 2 &&
                candidate.Parameters.All(parameter =>
                    ReferenceEquals(parameter.ParameterType, text)));
            Assert.Multiple(() =>
            {
                Assert.That(evidence!.Getter.Name, Is.EqualTo("GetNode"));
                Assert.That(evidence.NestedGetter.Name, Is.EqualTo("GetChoice"));
                Assert.That(evidence.TextField.Name, Is.EqualTo("Label"));
                Assert.That(evidence.FlagField.Name, Is.EqualTo("Flag"));
                Assert.That(evidence.FalseLiteral, Is.EqualTo("cool"));
                Assert.That(evidence.TrueLiteral, Is.EqualTo("warm"));
                Assert.That(evidence.Suffix, Is.EqualTo("!ending"));
                Assert.That(X64LiteralConcatProof.ProveConcat(twoArgumentConcat,
                    app), Is.True, "existing two-string Concat binding");
                Assert.That(X64LiteralConcatProof.ProveConcat(evidence.Concat,
                    app, 3), Is.True, "three-string Concat binding");
                Assert.That(X64LiteralConcatProof.ProveConcat(evidence.Concat,
                    app), Is.False, "arity must not be inferred from a call target");
            });
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(native, Has.Length.EqualTo(40));
            Assert.That(method.RawBytes.Length, Is.EqualTo(170));
            Assert.That(X64TripleLiteralGuardProof.TryProveShape(native),
                Is.Not.Null);

            // Each edited input is re-parsed from its own PE image. These
            // mutations separately break the guard exits, metadata initializer,
            // getter order, field binding, Boolean selection, and tail target.
            var pe = (PE)method.AppContext.Binary;
            foreach (var index in new[]
            {
                8, 11, 17, 18, 21, 23, 26, 28, 29, 31, 38, 39
            })
            {
                var changed = (byte[])image.Clone();
                var offset = checked((int)pe.MapVirtualAddressToRaw(
                    native[index].NextIP - 1, false));
                changed[offset] ^= 1;
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes,
                    UnityVersion.Parse("2021.3.35f1"));
                Assert.That(X64TripleLiteralGuardProof.Find(Compose()),
                    Is.Null, $"Native instruction {index} changed");
            }

            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(image, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            method = Compose();
            app = method.AppContext;
            var owner = app.GetAssemblyByName("TripleLiteralGuardFixture")!;
            var node = owner.Types.Single(type => type.Name == "LabelNode");
            var cell = owner.Types.Single(type => type.Name == "ChoiceCell");
            var label = node.Fields.Single(field => field.Name == "Label");
            var flag = cell.Fields.Single(field => field.Name == "Flag");
            try
            {
                label.OverrideOffset = label.DefaultOffset + 8;
                Assert.That(X64TripleLiteralGuardProof.Find(method), Is.Null);
            }
            finally { label.OverrideOffset = null; }
            try
            {
                flag.OverrideOffset = flag.DefaultOffset + 1;
                Assert.That(X64TripleLiteralGuardProof.Find(method), Is.Null);
            }
            finally { flag.OverrideOffset = null; }

            var getter = node.Methods.Single(candidate =>
                candidate.Name == "GetChoice");
            var bindings = app.MethodsByAddress[getter.UnderlyingPointer];
            try
            {
                bindings.Add(getter);
                Assert.That(X64TripleLiteralGuardProof.Find(method), Is.Null,
                    "A duplicate folded nested-getter binding is ambiguous");
            }
            finally { bindings.RemoveAt(bindings.Count - 1); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void ExactOneVolatileFieldControlsRemainUnresolved()
    {
        foreach (var kind in new[] { "STRING", "BOOLEAN" })
        {
            var directory = Environment.GetEnvironmentVariable(
                "CPP2IL_TRIPLE_LITERAL_GUARD_VOLATILE_" + kind + "_INPUT");
            if (string.IsNullOrEmpty(directory))
                Assert.Ignore("Set both one-volatile exact player inputs for this negative check.");
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
                var method = Compose();
                method.EnsureRawBytes();
                Assert.That(method.RawBytes.Length,
                    Is.EqualTo(kind == "STRING" ? 175 : 177));
                Assert.That(X64TripleLiteralGuardProof.Find(method), Is.Null,
                    $"Volatile {kind} field load matched the nonvolatile body");
            }
            finally { Cpp2IlApi.ResetInternalState(); }
        }
    }

    private static MethodAnalysisContext Compose() =>
        Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("TripleLiteralGuardFixture")!.Types
            .Single(type => type.Name == "LabelOwner").Methods
            .Single(method => method.Name == "Compose");
}
