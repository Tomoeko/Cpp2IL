using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64ClassCastAncestorInitializerProofTests
{
    [Test]
    public void ExactPlayerAllowsOnlyProvedNonTargetClassInitializers()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_VIRTUAL_STRING_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_VIRTUAL_STRING_CALL_FIXTURE_INPUT to the neutral exact player input.");

        var binary = Path.Combine(directory, "GameAssembly.dll");
        var metadata = Path.Combine(directory, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var types = app.GetAssemblyByName("VirtualStringCallFixture")!.Types;
            var hierarchy = app.GetAssemblyByName("Neutral.CastHierarchy")!.Types;
            var owner = types.Single(type => type.Name == "NodeOwner");
            var target = types.Single(type => type.Name == "DerivedNode");
            var middle = hierarchy.Single(type => type.Name == "MiddleNode");
            var outer = hierarchy.Single(type => type.Name == "OuterBase");
            var source = hierarchy.Single(type => type.Name == "ChainNode");
            var lookup = owner.Methods.Single(method => method.Name == "GetNode");
            var getter = types.Single(type => type.Name == "VirtualLabelOwner")
                .Methods.Single(method => method.Name == "get_Label");
            lookup.EnsureRawBytes();
            getter.EnsureRawBytes();
            var lookupBody = X86Utils.Iterate(lookup).ToArray();
            var getterBody = X86Utils.Iterate(getter).ToArray();
            var shape = X64ClassCastLookupProof.TryProveShape(lookupBody);

            Assert.Multiple(() =>
            {
                Assert.That(lookupBody.Length, Is.EqualTo(37));
                Assert.That(getterBody.Length, Is.EqualTo(20));
                Assert.That(shape, Is.Not.Null);
                Assert.That(target.Definition!.HasCctor, Is.False);
                Assert.That(middle.Definition!.HasCctor, Is.True);
                Assert.That(outer.Definition!.HasCctor, Is.True);
            });
            var proof = X64ClassCastLookupProof.Find(lookup, lookupBody);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.SourceField.DeclaringType, Is.SameAs(owner));
            Assert.That(proof.SourceField.Visibility, Is.EqualTo(FieldAttributes.Private));
            Assert.That(proof.SourceField.FieldType, Is.SameAs(source));
            Assert.That(target.DeclaringAssembly, Is.SameAs(owner.DeclaringAssembly));
            Assert.That(source.DeclaringAssembly, Is.Not.SameAs(owner.DeclaringAssembly));
            Assert.That(owner.DeclaringAssembly.Definition!.ReferencedAssemblies.Count(
                reference => ReferenceEquals(reference, source.DeclaringAssembly.Definition)),
                Is.EqualTo(1));
            Assert.That(X64LiteralConcatProof.Find(getter, getterBody), Is.Not.Null);

            var neighbor = owner.Fields.Single(field => field.Name == "Neighbor");
            Assert.That(X64ClassCastLookupProof.BindProvedShape(lookup,
                shape! with { FieldOffset = checked((int)neighbor.Offset) }), Is.Null,
                "A direct Int32 neighbor cannot replace the proved reference field.");

            var targetBits = target.Definition!.Bitfield;
            try
            {
                target.Definition.Bitfield = targetBits | (1u << 3);
                Assert.That(X64ClassCastLookupProof.Find(lookup, lookupBody), Is.Null,
                    "A target class initializer remains outside this proof.");
            }
            finally { target.Definition.Bitfield = targetBits; }

            var middleBits = middle.Definition!.Bitfield;
            try
            {
                middle.Definition.Bitfield = middleBits & ~(1u << 3);
                Assert.That(X64ClassCastLookupProof.Find(lookup, lookupBody), Is.Null,
                    "The permitted ancestor initializer must agree with metadata.");
            }
            finally { middle.Definition.Bitfield = middleBits; }

            var references = owner.DeclaringAssembly.Definition.ReferencedAssemblyCount;
            try
            {
                owner.DeclaringAssembly.Definition.ReferencedAssemblyCount = 0;
                Assert.That(X64ClassCastLookupProof.Find(lookup, lookupBody), Is.Null,
                    "The external source class must be a direct assembly reference.");
            }
            finally { owner.DeclaringAssembly.Definition.ReferencedAssemblyCount = references; }

            var sourceVersion = source.DeclaringAssembly.OverrideVersion;
            try
            {
                source.DeclaringAssembly.Version = new Version(99, 0);
                Assert.That(X64ClassCastLookupProof.Find(lookup, lookupBody), Is.Null,
                    "A changed external assembly identity is outside the proof.");
            }
            finally { source.DeclaringAssembly.OverrideVersion = sourceVersion; }

            Assert.That(X64ClassCastLookupProof.Find(lookup, lookupBody), Is.Not.Null,
                "Negative mutations must leave the authenticated model unchanged.");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
