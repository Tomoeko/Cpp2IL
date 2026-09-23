using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional integration check against the separately built synthetic native fixture.</summary>
[TestFixture]
[NonParallelizable]
public class CanonicalFinalizerOverrideTests
{
    [Test]
    public void RequiresNativeFinalizerEvidenceAndPreservesOnlyCanonicalRelationships()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FINALIZER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FINALIZER_FIXTURE_INPUT to the exact synthetic FinalizerFixture player-input directory.");

        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True,
            "The supplied directory must contain the neutral fixture's native player inputs; no fallback is used.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            Assert.That(app.MetadataVersion, Is.EqualTo(29));
            var assembly = app.GetAssemblyByName("FinalizerFixture");
            Assert.That(assembly, Is.Not.Null, "Build the public Validation/FinalizerFixture source first.");
            var types = assembly!.Types.Where(type => type.Name != "<Module>").ToArray();
            Assert.That(types.Select(type => type.FullName), Is.EquivalentTo(new[]
            {
                "FinalizerFixture.Finalizable", "FinalizerFixture.InheritedFinalizer", "FinalizerFixture.DerivedFinalizer",
                "FinalizerFixture.VirtualBase", "FinalizerFixture.VirtualOverride", "FinalizerFixture.NewSlot",
                "FinalizerFixture.FinalizeOverload",
            }));
            Assert.That(types.Sum(type => type.Methods.Count), Is.EqualTo(13));
            Assert.That(types.Sum(type => type.Fields.Count), Is.EqualTo(1));

            foreach (var candidate in types)
                Assert.That(HasMapping(candidate), Is.EqualTo(candidate.Name is "Finalizable" or "DerivedFinalizer"),
                    "Only the two declared destructors may receive canonical mappings: " + candidate.Name);

            var type = assembly.GetTypeByFullName("FinalizerFixture.Finalizable")!;
            var method = type.Methods.Single(candidate => candidate.Name == "Finalize");
            var native = type.Definition!;
            var originalBits = native.Bitfield;
            native.Bitfield &= ~(1u << 2);
            Assert.That(HasMapping(type), Is.False, "The runtime finalizer flag is required.");
            native.Bitfield = originalBits;

            var attributes = method.Attributes;
            foreach (var invalid in new[]
                     {
                         attributes | MethodAttributes.NewSlot,
                         attributes | MethodAttributes.Static,
                         (attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Private,
                     })
            {
                method.Attributes = invalid;
                Assert.That(HasMapping(type), Is.False, "Reject noncanonical method flags: " + invalid);
            }
            method.Attributes = attributes;

            var slot = method.Definition!.slot;
            method.Definition.slot = ushort.MaxValue;
            Assert.That(HasMapping(type), Is.False, "An absent slot does not establish the override.");
            method.Definition.slot = slot;

            var offset = native.VtableStart + slot;
            var entry = app.Metadata.VTableMethodIndices[offset];
            app.Metadata.VTableMethodIndices[offset] = app.Metadata.VTableMethodIndices[native.VtableStart];
            Assert.That(HasMapping(type), Is.False, "The finalizer slot must contain this method's body.");
            app.Metadata.VTableMethodIndices[offset] = entry;

            var returnType = method.ReturnType;
            method.ReturnType = app.SystemTypes.SystemInt32Type;
            Assert.That(HasMapping(type), Is.False, "Finalize must return void.");
            method.ReturnType = returnType;
            method.Parameters.Add(new InjectedParameterAnalysisContext("value", app.SystemTypes.SystemInt32Type,
                ParameterAttributes.None, 0, method));
            Assert.That(HasMapping(type), Is.False, "Finalize must have no explicit parameters.");
            method.Parameters.RemoveAt(method.Parameters.Count - 1);

            type.Methods.Add(method);
            Assert.That(HasMapping(type), Is.False, "Ambiguous method candidates must not be selected.");
            type.Methods.RemoveAt(type.Methods.Count - 1);
            Assert.That(HasMapping(type), Is.True, "Restored native evidence must still qualify.");

            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var managed = type.GetExtraData<TypeDefinition>("AsmResolverType")!;
            Assert.That(managed.MethodImplementations, Has.Count.EqualTo(1));
            CanonicalFinalizerOverride.AddTo(managed, type);
            Assert.That(managed.MethodImplementations, Has.Count.EqualTo(1), "Repeated emission must not duplicate the relationship.");
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }

    private static bool HasMapping(TypeAnalysisContext type) => CanonicalFinalizerOverride.Resolve(type) != null;
}
