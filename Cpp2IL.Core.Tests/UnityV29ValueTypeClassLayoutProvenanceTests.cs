using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Cpp2IL.Core.SourceEmission;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.Tests;

[TestFixture]
public class UnityV29ValueTypeClassLayoutProvenanceTests
{
    [Test]
    public void IdenticalPlayerFactsCannotEstablishWhetherNaturalSizeWasDeclared()
    {
        // The public exact-target fixture gives both authored Size=0 and Size=6
        // the same type bitfield and effective native size. Size=8 is a control.
        const uint valueTypeBits = 8337;
        var omittedSize = new Il2CppTypeDefinition { Bitfield = valueTypeBits };
        var explicitNaturalSize = new Il2CppTypeDefinition { Bitfield = valueTypeBits };
        var explicitLargerSize = new Il2CppTypeDefinition { Bitfield = valueTypeBits };

        Assert.Multiple(() =>
        {
            Assert.That(omittedSize.ClassSizeIsDefault, Is.False);
            Assert.That(explicitNaturalSize.Bitfield, Is.EqualTo(omittedSize.Bitfield));
            Assert.That(UnityV29ValueTypeClassLayoutProvenance.HasUnknownDeclaredClassSize(omittedSize), Is.True);
            Assert.That(UnityV29ValueTypeClassLayoutProvenance.HasUnknownDeclaredClassSize(explicitNaturalSize), Is.True);
            Assert.That(UnityV29ValueTypeClassLayoutProvenance.HasUnknownDeclaredClassSize(explicitLargerSize), Is.True);
            Assert.That(UnityV29ValueTypeClassLayoutProvenance.HasUnknownDeclaredClassSize(
                new Il2CppTypeDefinition { Bitfield = valueTypeBits | (1u << 11) }), Is.False);
            Assert.That(UnityV29ValueTypeClassLayoutProvenance.HasUnknownDeclaredClassSize(
                new Il2CppTypeDefinition { Bitfield = valueTypeBits | (1u << 1) }), Is.False);
            Assert.That(UnityV29ValueTypeClassLayoutProvenance.HasUnknownDeclaredClassSize(
                new Il2CppTypeDefinition { Bitfield = valueTypeBits & ~1u }), Is.False,
                "Reference-class layout needs a separate exact-target fixture and recovery analysis.");
        });
    }

    [Test]
    public void ReportSeparatesRawNativeSizesFromWriterCandidatesAndUnknownDeclarations()
    {
        var path = Path.Combine(Path.GetTempPath(), "cpp2il-layout-report-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var report = new UnitySourceEmissionReport { SourceGeneration = "generated" };
            UnityV29ValueTypeClassLayoutProvenance.AddToReport(report, [new UnityValueTypeClassLayoutAssemblyReport("Synthetic.Application",
            [
                new UnityValueTypeClassLayoutTypeReport("Synthetic.ImplicitSize", 6),
                new UnityValueTypeClassLayoutTypeReport("Synthetic.ExplicitNaturalSize", 6),
                new UnityValueTypeClassLayoutTypeReport("Synthetic.ExplicitLargerSize", 8),
                new UnityValueTypeClassLayoutTypeReport("Synthetic.UnknownNativeSize", -1),
                new UnityValueTypeClassLayoutTypeReport("Synthetic.ZeroNativeSize", 0),
            ])]);
            report.WriteJson(path);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var layout = root.GetProperty("ValueTypeClassLayoutMetadata")[0];
            var types = layout.GetProperty("Types").EnumerateArray().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("SchemaVersion").GetInt32(), Is.EqualTo(5));
                Assert.That(root.GetProperty("SourceGeneration").GetString(), Is.EqualTo("generated"));
                Assert.That(root.GetProperty("DeclarationFidelity").GetString(), Is.EqualTo("partial"));
                Assert.That(root.GetProperty("Diagnostics").GetArrayLength(), Is.Zero);
                Assert.That(root.GetProperty("DeclarationDiagnostics")[0].GetString(), Does.StartWith("DECL002:"));
                Assert.That(layout.GetProperty("Name").GetString(), Is.EqualTo("Synthetic.Application"));
                Assert.That(layout.GetProperty("UnknownDeclaredClassSizeCount").GetInt32(), Is.EqualTo(5));
                Assert.That(layout.GetProperty("EvidenceSource").GetString(), Is.EqualTo("v29-player-type-and-native-size"));
                Assert.That(types.Select(type => type.GetProperty("RawNativeSize").GetInt32()), Is.EqualTo(new[] { 6, 6, 8, -1, 0 }));
                Assert.That(types.Select(type => type.GetProperty("EmittedClassSizeCandidate").GetUInt32()), Is.EqualTo(new uint[] { 6, 6, 8, 0, 0 }));
                Assert.That(types.Select(type => type.GetProperty("DeclaredClassSize").GetString()),
                    Is.All.EqualTo("UnknownDeclaredClassSize"));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void NoAffectedTypesDoNotChangeDeclarationStatus()
    {
        var report = new UnitySourceEmissionReport { SourceGeneration = "generated" };
        UnityV29ValueTypeClassLayoutProvenance.AddToReport(report, []);

        Assert.Multiple(() =>
        {
            Assert.That(report.DeclarationFidelity, Is.EqualTo("unverified"));
            Assert.That(report.DeclarationDiagnostics, Is.Empty);
            Assert.That(report.ValueTypeClassLayoutMetadata, Is.Empty);
            Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
        });
    }
}
