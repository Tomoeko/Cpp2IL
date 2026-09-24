using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AsmResolver.DotNet;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.SourceEmission;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.Tests;

[TestFixture]
public class UnityV29ReferenceClassLayoutProvenanceTests
{
    [Test]
    public void ClassPairWithIdenticalPlayerFactsIsReportedButUnrelatedTypesAreExcluded()
    {
        // The public exact-target class pair has the same bitfield and native size
        // with authored Size=0 and Size=6. The explicit Size=32 control is included.
        const uint classBits = 8336;
        var omittedSize = new Il2CppTypeDefinition { Bitfield = classBits };
        var explicitNaturalSize = new Il2CppTypeDefinition { Bitfield = classBits };
        var explicitLargerSize = new Il2CppTypeDefinition { Bitfield = classBits };
        var packOnly = new Il2CppTypeDefinition
        {
            Bitfield = classBits | (1u << 11), Flags = (uint)TypeAttributes.SequentialLayout,
        };
        var sizeOnly = new Il2CppTypeDefinition { Bitfield = classBits | (1u << 10) };

        Assert.Multiple(() =>
        {
            Assert.That(omittedSize.ClassSizeIsDefault, Is.False);
            Assert.That(explicitNaturalSize.Bitfield, Is.EqualTo(omittedSize.Bitfield));
            Assert.That(UnityV29ReferenceClassLayoutProvenance.HasUnemittedClassLayout(omittedSize), Is.True);
            Assert.That(UnityV29ReferenceClassLayoutProvenance.HasUnemittedClassLayout(explicitNaturalSize), Is.True);
            Assert.That(UnityV29ReferenceClassLayoutProvenance.HasUnemittedClassLayout(explicitLargerSize), Is.True);
            Assert.That(packOnly.ClassSizeIsDefault, Is.True);
            Assert.That(packOnly.PackingSizeIsDefault, Is.False);
            Assert.That(UnityV29ReferenceClassLayoutProvenance.HasUnemittedClassLayout(packOnly), Is.False);
            Assert.That(UnityV29ReferenceClassLayoutProvenance.HasUnemittedClassLayout(sizeOnly), Is.True);
            Assert.That(UnityV29ReferenceClassLayoutProvenance.HasUnemittedClassLayout(
                new Il2CppTypeDefinition { Bitfield = classBits | (1u << 11) }), Is.True,
                "An auto-layout class does not prove an authored packing declaration.");
            Assert.That(UnityV29ReferenceClassLayoutProvenance.HasUnemittedClassLayout(
                new Il2CppTypeDefinition
                {
                    Bitfield = (classBits & ~(0xfu << 12)) | (1u << 11),
                    Flags = (uint)TypeAttributes.SequentialLayout,
                }), Is.True, "Zero specified packing is not a proved nondefault Pack value.");
            Assert.That(UnityV29ReferenceClassLayoutProvenance.HasUnemittedClassLayout(
                new Il2CppTypeDefinition { Bitfield = classBits | (1u << 10) | (1u << 11) }), Is.False);
            Assert.That(UnityV29ReferenceClassLayoutProvenance.HasUnemittedClassLayout(
                new Il2CppTypeDefinition { Bitfield = classBits | 1u }), Is.False);
            Assert.That(UnityV29ReferenceClassLayoutProvenance.HasUnemittedClassLayout(
                new Il2CppTypeDefinition { Bitfield = classBits, Flags = (uint)TypeAttributes.Interface }), Is.False);
        });
    }

    [Test]
    public void MissingClassLayoutReportsUnknownSizeAndStrictSourceRejection()
    {
        var path = Path.Combine(Path.GetTempPath(), "cpp2il-class-layout-report-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var report = new UnitySourceEmissionReport { SourceGeneration = "generated" };
            var classDefinition = new Il2CppTypeDefinition { Bitfield = 8336 };
            var packOnlyDefinition = new Il2CppTypeDefinition
            {
                Bitfield = 8336 | (1u << 11), Flags = (uint)TypeAttributes.SequentialLayout,
            };
            UnityV29ReferenceClassLayoutProvenance.AddToReport(report, [new UnityReferenceClassLayoutAssemblyReport("Synthetic.Application",
            [
                UnityV29ReferenceClassLayoutProvenance.CreateTypeReport("Synthetic.ImplicitClassSize", classDefinition, 6),
                UnityV29ReferenceClassLayoutProvenance.CreateTypeReport("Synthetic.ExplicitNaturalClassSize", classDefinition, 6),
                UnityV29ReferenceClassLayoutProvenance.CreateTypeReport("Synthetic.ExplicitLargerClassSize", classDefinition, 32),
                UnityV29ReferenceClassLayoutProvenance.CreateTypeReport("Synthetic.PackOnlyClass", packOnlyDefinition, 6),
            ])]);
            report.WriteJson(path);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var layout = root.GetProperty("ReferenceClassLayoutMetadata")[0];
            var types = layout.GetProperty("Types").EnumerateArray().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("SchemaVersion").GetInt32(), Is.EqualTo(5));
                Assert.That(root.GetProperty("SourceGeneration").GetString(), Is.EqualTo("partial"));
                Assert.That(root.GetProperty("DeclarationFidelity").GetString(), Is.EqualTo("partial"));
                Assert.That(root.GetProperty("DeclarationDiagnostics")[0].GetString(), Does.StartWith("DECL003:"));
                Assert.That(root.GetProperty("Diagnostics")[0].GetString(), Does.StartWith("SOURCE011: Synthetic.Application:"));
                Assert.That(layout.GetProperty("UnemittedClassLayoutCount").GetInt32(), Is.EqualTo(3));
                Assert.That(layout.GetProperty("UnknownDeclaredClassSizeCount").GetInt32(), Is.EqualTo(3));
                Assert.That(layout.GetProperty("EvidenceSource").GetString(), Is.EqualTo("v29-player-type-and-current-dll-writer"));
                Assert.That(types.Select(type => type.GetProperty("RawNativeSize").GetInt32()), Is.EqualTo(new[] { 6, 6, 32, 6 }));
                Assert.That(types.Select(type => type.GetProperty("PackingSizeIsDefault").GetBoolean()), Is.EqualTo(new[] { false, false, false, false }));
                Assert.That(types.Select(type => type.GetProperty("ClassSizeIsDefault").GetBoolean()), Is.EqualTo(new[] { false, false, false, true }));
                Assert.That(types.Select(type => type.GetProperty("PlayerPackingSize").GetUInt32()), Is.EqualTo(new uint[] { 2, 2, 2, 2 }));
                Assert.That(types.Select(type => type.GetProperty("PlayerSpecifiedPackingSize").GetUInt32()), Is.EqualTo(new uint[] { 2, 2, 2, 2 }));
                Assert.That(types.Select(type => type.GetProperty("DeclaredClassSize").GetString()),
                    Is.EqualTo(new[] { "UnknownDeclaredClassSize", "UnknownDeclaredClassSize", "UnknownDeclaredClassSize", "NoNondefaultClassSizeFlag" }));
                Assert.That(types.Select(type => type.GetProperty("ClassLayoutEmission").GetString()),
                    Is.EqualTo(new[] { "ClassLayoutRowOmitted", "ClassLayoutRowOmitted", "ClassLayoutRowOmitted",
                        "PackOnlyClassLayoutRowEmitted" }));
                Assert.That(types.All(type => !type.TryGetProperty("EmittedClassSizeCandidate", out _)), Is.True);
            });
            Assert.Throws<InvalidOperationException>(() => UnityCsOutputFormat.EnsureCompleteSourceGeneration(report));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ProvenPackOnlyClassEmitsRowWithoutClaimingUnknownDeclaredSize()
    {
        var packOnly = new Il2CppTypeDefinition
        {
            Bitfield = 8336 | (1u << 11), Flags = (uint)TypeAttributes.SequentialLayout,
        };
        var managed = new TypeDefinition("Synthetic", "PackOnlyClass",
            (AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes)TypeAttributes.SequentialLayout);
        AsmResolverDllOutputFormat.ConfigureTypeLayout(packOnly, managed);
        var unknownSize = new Il2CppTypeDefinition
        {
            Bitfield = 8336, Flags = (uint)TypeAttributes.SequentialLayout,
        };
        var unresolved = new TypeDefinition("Synthetic", "UnknownSizeClass",
            (AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes)TypeAttributes.SequentialLayout);
        AsmResolverDllOutputFormat.ConfigureTypeLayout(unknownSize, unresolved);
        var type = UnityV29ReferenceClassLayoutProvenance.CreateTypeReport("Synthetic.PackOnlyClass", packOnly, 6);
        var report = new UnitySourceEmissionReport { SourceGeneration = "generated" };
        UnityV29ReferenceClassLayoutProvenance.AddToReport(report,
            [new UnityReferenceClassLayoutAssemblyReport("Synthetic.Application", [type])]);

        Assert.Multiple(() =>
        {
            Assert.That(managed.ClassLayout?.PackingSize, Is.EqualTo(2));
            Assert.That(managed.ClassLayout?.ClassSize, Is.Zero);
            Assert.That(unresolved.ClassLayout, Is.Null,
                "An ambiguous declared Size must not be filled from native size.");
            Assert.That(report.ReferenceClassLayoutMetadata.Single().UnemittedClassLayoutCount, Is.Zero);
            Assert.That(report.ReferenceClassLayoutMetadata.Single().UnknownDeclaredClassSizeCount, Is.Zero);
            Assert.That(type.DeclaredClassSize, Is.EqualTo("NoNondefaultClassSizeFlag"));
            Assert.That(type.ClassLayoutEmission, Is.EqualTo("PackOnlyClassLayoutRowEmitted"));
            Assert.That(report.DeclarationDiagnostics, Is.Empty);
            Assert.That(report.DeclarationFidelity, Is.EqualTo("unverified"));
            Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
            Assert.That(report.Diagnostics, Is.Empty);
            Assert.DoesNotThrow(() => UnityCsOutputFormat.EnsureCompleteSourceGeneration(report));
        });
    }

    [Test]
    public void ProvenPackOnlyClassAppearsInGeneratedCSharp()
    {
        const string assemblyName = "Synthetic.Application";
        var coreName = typeof(object).Assembly.GetName();
        var coreReference = new AssemblyReference(coreName.Name, coreName.Version!)
        {
            PublicKeyOrToken = coreName.GetPublicKeyToken(),
        };
        var module = new ModuleDefinition(assemblyName + ".dll", coreReference);
        var assembly = new AssemblyDefinition(assemblyName, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        var managed = new TypeDefinition("Synthetic", "PackOnlyClass",
            AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public |
            AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.SequentialLayout,
            module.CorLibTypeFactory.Object.Type);
        var player = new Il2CppTypeDefinition
        {
            Bitfield = 8336 | (1u << 11), Flags = (uint)TypeAttributes.SequentialLayout,
        };
        AsmResolverDllOutputFormat.ConfigureTypeLayout(player, managed);
        module.TopLevelTypes.Add(managed);

        var output = Path.Combine(Path.GetTempPath(), "cpp2il-pack-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = UnitySourceProjectEmitter.Emit([assembly], [assemblyName],
                [Path.GetDirectoryName(typeof(object).Assembly.Location)!], output, playerMetadataVersion: 29f);
            var source = File.ReadAllText(Path.Combine(output, report.Assemblies.Single().SourceFile));
            Assert.Multiple(() =>
            {
                Assert.That(source, Does.Contain("StructLayout(LayoutKind.Sequential, Pack = 2)"));
                Assert.That(source, Does.Not.Contain("Size ="));
                Assert.That(source, Does.Contain("namespace Synthetic"));
                Assert.That(source, Does.Not.Contain("namespace Synthetic;"));
                Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
                Assert.That(report.Diagnostics, Is.Empty);
            });
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, true);
        }
    }

    [Test]
    public void ExistingIncompleteSourceStatusIsPreserved()
    {
        var packOnly = new Il2CppTypeDefinition { Bitfield = 8336 | (1u << 11) };
        var report = new UnitySourceEmissionReport { SourceGeneration = "incomplete" };
        UnityV29ReferenceClassLayoutProvenance.AddToReport(report,
            [new UnityReferenceClassLayoutAssemblyReport("Synthetic.Application",
                [UnityV29ReferenceClassLayoutProvenance.CreateTypeReport("Synthetic.PackOnlyClass", packOnly, 6)])]);

        Assert.Multiple(() =>
        {
            Assert.That(report.SourceGeneration, Is.EqualTo("incomplete"));
            Assert.That(report.Diagnostics.Single(), Does.StartWith("SOURCE011:"));
            Assert.Throws<InvalidOperationException>(() => UnityCsOutputFormat.EnsureCompleteSourceGeneration(report));
        });
    }

    [Test]
    public void NoAffectedReferenceClassesLeaveStrictSourceEligibilityUnchanged()
    {
        var report = new UnitySourceEmissionReport { SourceGeneration = "generated" };
        UnityV29ReferenceClassLayoutProvenance.AddToReport(report, []);

        Assert.Multiple(() =>
        {
            Assert.That(report.ReferenceClassLayoutMetadata, Is.Empty);
            Assert.That(report.DeclarationDiagnostics, Is.Empty);
            Assert.That(report.Diagnostics, Is.Empty);
            Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
            Assert.That(report.DeclarationFidelity, Is.EqualTo("unverified"));
            Assert.DoesNotThrow(() => UnityCsOutputFormat.EnsureCompleteSourceGeneration(report));
        });
    }
}
