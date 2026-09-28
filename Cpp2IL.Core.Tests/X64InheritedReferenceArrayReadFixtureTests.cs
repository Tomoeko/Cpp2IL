using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64InheritedReferenceArrayReadFixtureTests
{
    [Test]
    public void InheritedReadRequiresTheExactUnshadowedBaseFieldAndLayout()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_INHERITED_REFERENCE_ARRAY_READ_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_INHERITED_REFERENCE_ARRAY_READ_FIXTURE_INPUT to the neutral exact player input.");
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
            var assembly = app.GetAssemblyByName("InheritedReferenceArrayReadFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "DerivedCatalog");
            var baseType = assembly.Types.Single(type => type.Name == "BaseCatalog");
            var field = baseType.Fields.Single(candidate => candidate.Name == "Items");
            var first = owner.Methods.Single(method => method.Name == "get_First");
            var second = owner.Methods.Single(method => method.Name == "get_Second");
            var access = new FieldReference(field,
                new LocalVariable("owner",
                    new Cpp2IL.Core.ISIL.Register(null, "owner"), owner),
                checked((int)field.Offset));

            Assert.Multiple(() =>
            {
                Assert.That(owner.BaseType, Is.SameAs(baseType));
                Assert.That(NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access),
                    Is.True);
                Assert.That(X64FixedReferenceArrayReadProof.Find(first)?.ArrayField,
                    Is.SameAs(field));
                Assert.That(X64FixedReferenceArrayReadProof.Find(first)?.Index,
                    Is.EqualTo(0));
                Assert.That(X64FixedReferenceArrayReadProof.Find(second)?.ArrayField,
                    Is.SameAs(field));
                Assert.That(X64FixedReferenceArrayReadProof.Find(second)?.Index,
                    Is.EqualTo(1));
            });

            try
            {
                field.OverrideOffset = field.DefaultOffset + 8;
                Assert.That(X64FixedReferenceArrayReadProof.Find(first), Is.Null);
            }
            finally { field.OverrideOffset = null; }

            try
            {
                field.OverrideFieldType = app.SystemTypes.SystemObjectType;
                Assert.That(X64FixedReferenceArrayReadProof.Find(first), Is.Null);
            }
            finally { field.OverrideFieldType = null; }

            try
            {
                field.Visibility = FieldAttributes.Private;
                Assert.That(X64FixedReferenceArrayReadProof.Find(first), Is.Null);
            }
            finally { field.OverrideAttributes = null; }

            var shadow = new InjectedFieldAnalysisContext("Items",
                app.SystemTypes.SystemObjectType, FieldAttributes.Public, owner,
                checked((int)field.Offset + 24));
            try
            {
                owner.Fields.Add(shadow);
                Assert.That(NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access),
                    Is.True, "the separate source-name ambiguity gate must reject the shadow");
                Assert.That(X64FixedReferenceArrayReadProof.Find(first), Is.Null);
            }
            finally { owner.Fields.Remove(shadow); }

            var overlap = new InjectedFieldAnalysisContext("Overlap",
                app.SystemTypes.SystemInt32Type, FieldAttributes.Public, owner,
                checked((int)field.Offset + 4));
            try
            {
                owner.Fields.Add(overlap);
                Assert.That(NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access),
                    Is.False);
                Assert.That(X64FixedReferenceArrayReadProof.Find(first), Is.Null);
            }
            finally { owner.Fields.Remove(overlap); }

            Assert.That(X64FixedReferenceArrayReadProof.Find(first)?.ArrayField,
                Is.SameAs(field));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
