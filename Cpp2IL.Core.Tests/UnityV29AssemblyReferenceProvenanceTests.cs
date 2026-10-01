using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AssetRipper.Primitives;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.SourceEmission;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.Tests;

[TestFixture, NonParallelizable]
public class UnityV29AssemblyReferenceProvenanceTests
{
    private sealed class PlayerRows
    {
        internal byte[] Bytes { get; }
        internal Il2CppGlobalMetadataHeader Header { get; }
        internal Il2CppAssemblyDefinition[] Definitions { get; }
        internal int[] References = [1, 1, 0];
        internal int AssemblyOffset { get; }
        internal int ReferenceOffset { get; }

        internal PlayerRows()
        {
            var strings = Encoding.UTF8.GetBytes("\0Synthetic.Application\0Synthetic.Dependency\0en\0");
            var keyIndex = strings.Length;
            strings = strings.Concat(new byte[] { 3, 0x41, 0x42, 0x43 }).ToArray();
            AssemblyOffset = (256 + strings.Length + 3) & ~3;
            ReferenceOffset = AssemblyOffset + 128;
            Bytes = new byte[ReferenceOffset + 12];
            using var stream = new MemoryStream(Bytes, true);
            using var writer = new BinaryWriter(stream);
            writer.Write(Il2CppMetadata.MetadataMagic);
            writer.Write(29);
            WriteSection(writer, 24, 256, strings.Length);
            WriteSection(writer, 176, AssemblyOffset, 128);
            WriteSection(writer, 192, ReferenceOffset, 12);
            stream.Position = 256;
            writer.Write(strings);
            Definitions =
            [
                Definition(0, 0, 3, 1, 0, 0, 1, 2, 3, 4, 0),
                Definition(1, 3, 0, 23, 43, keyIndex, 5, 6, 7, 8, 0x0807060504030201),
            ];
            // Derive indices from the authored ASCII string layout.
            Definitions[1].AssemblyName.nameIndex = Array.IndexOf(strings, (byte)'S', 2);
            Definitions[1].AssemblyName.cultureIndex = keyIndex - 3;
            stream.Position = AssemblyOffset;
            foreach (var definition in Definitions) WriteDefinition(writer, definition);
            stream.Position = ReferenceOffset;
            foreach (var index in References) writer.Write(index);
            Header = new()
            {
                magicNumber = Il2CppMetadata.MetadataMagic, version = 29,
                @string = new() { Offset = 256, Size = strings.Length },
                assemblies = new() { Offset = AssemblyOffset, Size = 128 },
                referencedAssemblies = new() { Offset = ReferenceOffset, Size = 12 },
            };
        }

        internal UnityV29AssemblyReferenceProvenance.Table? Capture() =>
            UnityV29AssemblyReferenceProvenance.Capture(29, Bytes.Length,
                (offset, count) => Bytes.AsSpan(checked((int)offset), count).ToArray(), Header, Definitions, References);

        internal void WriteInt(int offset, int value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);

        private static Il2CppAssemblyDefinition Definition(int image, int start, int count,
            int name, int culture, int key, int major, int minor, int build, int revision, ulong token) => new()
        {
            ImageIndex = image, Token = 0x20000001, ReferencedAssemblyStart = start, ReferencedAssemblyCount = count,
            AssemblyName = new()
            {
                nameIndex = name, cultureIndex = culture, publicKeyIndex = key, hash_alg = 0x8004,
                hash_len = 3, flags = 1, major = major, minor = minor, build = build, revision = revision,
                publicKeyToken = token,
            },
        };

        private static void WriteSection(BinaryWriter writer, int headerOffset, int offset, int size)
        {
            writer.BaseStream.Position = headerOffset;
            writer.Write(offset);
            writer.Write(size);
        }

        private static void WriteDefinition(BinaryWriter writer, Il2CppAssemblyDefinition definition)
        {
            var name = definition.AssemblyName;
            writer.Write(definition.ImageIndex); writer.Write(definition.Token);
            writer.Write(definition.ReferencedAssemblyStart); writer.Write(definition.ReferencedAssemblyCount);
            writer.Write(name.nameIndex); writer.Write(name.cultureIndex); writer.Write(name.publicKeyIndex);
            writer.Write(name.hash_alg); writer.Write(name.hash_len); writer.Write(name.flags);
            writer.Write(name.major); writer.Write(name.minor); writer.Write(name.build); writer.Write(name.revision);
            writer.Write(name.publicKeyToken);
        }
    }

    [Test]
    public void ResolvedDefinitionIdentityPreservesOrderedDuplicateIndicesWithoutGuessingOriginalRows()
    {
        var player = new PlayerRows();
        var table = player.Capture();
        Assert.That(table, Is.Not.Null);
        var report = table!.CreateReport("Synthetic.Application", 0);
        Assert.Multiple(() =>
        {
            Assert.That(report.OriginalManagedAssemblyRefIdentity, Is.EqualTo("UnavailableInPlayerSchema"));
            Assert.That(report.ResolvedReferences.Select(reference => reference.ResolvedAssemblyDefinitionOrdinal),
                Is.EqualTo(new[] { 1, 1, 0 }));
            Assert.That(report.ResolvedReferences.Select(reference => reference.ReferenceTableOrdinal), Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(report.ResolvedReferences[0].ResolvedIdentity.Name, Is.EqualTo("Synthetic.Dependency"));
            Assert.That(report.ResolvedReferences[0].ResolvedIdentity.Culture, Is.EqualTo("en"));
            Assert.That(report.ResolvedReferences[0].ResolvedIdentity.RawDefinitionVersion, Is.EqualTo("5.6.7.8"));
            Assert.That(report.ResolvedReferences[0].ResolvedIdentity.PublicKey, Is.EqualTo("414243"));
            Assert.That(report.ResolvedReferences[0].ResolvedIdentity.PublicKeyToken, Is.EqualTo("0102030405060708"));
            Assert.That(table.CreateReport("Synthetic.Dependency", 1).ResolvedReferences, Is.Empty);
        });
    }

    [TestCase("raw-index")]
    [TestCase("cached-index")]
    [TestCase("out-of-range-index")]
    [TestCase("header-size")]
    [TestCase("section-bounds")]
    [TestCase("row-width")]
    [TestCase("reference-span")]
    [TestCase("raw-version")]
    [TestCase("cached-token")]
    [TestCase("cached-hash-length")]
    [TestCase("cached-image")]
    [TestCase("cached-row-token")]
    [TestCase("key-bounds")]
    [TestCase("name-bounds")]
    public void RawAndCachedDisagreementsCannotProduceAuthenticatedIdentities(string mutation)
    {
        var player = new PlayerRows();
        Assert.That(player.Capture(), Is.Not.Null);
        switch (mutation)
        {
            case "raw-index": player.WriteInt(player.ReferenceOffset, 0); break;
            case "cached-index": player.References[0] = 0; break;
            case "out-of-range-index": player.WriteInt(player.ReferenceOffset, 2); player.References[0] = 2; break;
            case "header-size": player.Header.assemblies.Size -= 64; break;
            case "section-bounds": player.WriteInt(196, int.MaxValue); break;
            case "row-width": player.WriteInt(180, 127); player.Header.assemblies.Size = 127; break;
            case "reference-span": player.WriteInt(player.AssemblyOffset + 12, 4); player.Definitions[0].ReferencedAssemblyCount = 4; break;
            case "raw-version": player.WriteInt(player.AssemblyOffset + 64 + 40, 9); break;
            case "cached-token": player.Definitions[1].AssemblyName.publicKeyToken++; break;
            case "cached-hash-length": player.Definitions[1].AssemblyName.hash_len++; break;
            case "cached-image": player.Definitions[1].ImageIndex++; break;
            case "cached-row-token": player.Definitions[1].Token++; break;
            case "key-bounds":
                var key = player.Definitions[1].AssemblyName.publicKeyIndex;
                player.Bytes[256 + key] = 127;
                break;
            case "name-bounds":
                player.WriteInt(player.AssemblyOffset + 64 + 16, int.MaxValue);
                player.Definitions[1].AssemblyName.nameIndex = int.MaxValue;
                break;
            default: throw new ArgumentException(nameof(mutation));
        }
        Assert.That(player.Capture(), Is.Null);
    }

    [Test]
    [TestCase("header")]
    [TestCase("definitions")]
    [TestCase("references")]
    public void MissingMutableMetadataCachesCannotBecomeAuthenticatedProvenance(string missing)
    {
        var player = new PlayerRows();
        Assert.That(player.Capture(), Is.Not.Null);
        var rejected = UnityV29AssemblyReferenceProvenance.Capture(29, player.Bytes.Length,
            (offset, count) => player.Bytes.AsSpan(checked((int)offset), count).ToArray(),
            missing == "header" ? null : player.Header,
            missing == "definitions" ? null : player.Definitions,
            missing == "references" ? null : player.References);
        Assert.That(rejected, Is.Null);
    }

    [Test]
    public void EmptyResolvedGraphDoesNotEstablishOriginalManagedReferenceRowAbsence()
    {
        var report = new UnitySourceEmissionReport { SourceGeneration = "generated" };
        UnityV29AssemblyReferenceProvenance.AddToReport(report,
            [new PlayerRows().Capture()!.CreateReport("Synthetic.Dependency", 1)]);
        Assert.That(report.AssemblyReferenceMetadata.Single().ResolvedReferences, Is.Empty);
        Assert.That(report.AssemblyReferenceMetadata.Single().OriginalManagedAssemblyRefIdentity, Is.EqualTo("UnavailableInPlayerSchema"));
        Assert.That(report.DeclarationFidelity, Is.EqualTo("partial"));
        Assert.That(report.DeclarationDiagnostics.Single(), Does.StartWith("DECL006:"));
    }

    [Test]
    public void SnapshotRejectsEqualValuedRowReplacementAndCoherentlyChangedInput()
    {
        var player = new PlayerRows();
        var captured = player.Capture()!;
        var previous = player.Definitions[1];
        player.Definitions[1] = new()
        {
            ImageIndex = previous.ImageIndex, Token = previous.Token,
            ReferencedAssemblyStart = previous.ReferencedAssemblyStart, ReferencedAssemblyCount = previous.ReferencedAssemblyCount,
            AssemblyName = previous.AssemblyName,
        };
        Assert.That(player.Capture(), Is.Not.Null, "A fresh report may use a semantically identical cache row.");
        Assert.That(captured.Matches(player.Capture()), Is.False, "Saved row identity must not silently change.");
        player.Definitions[1] = previous;
        Assert.That(captured.Matches(player.Capture()), Is.True);
        player.WriteInt(player.ReferenceOffset, 0);
        player.References[0] = 0;
        Assert.That(player.Capture(), Is.Not.Null);
        Assert.That(captured.Matches(player.Capture()), Is.False, "Coherent new bytes are a new observation, not the saved snapshot.");
    }

    [Test]
    public void ValidUnknownDeclarationProvenanceAndInvalidBindingsHaveSeparateStrictDispositions()
    {
        var path = Path.Combine(Path.GetTempPath(), "cpp2il-reference-provenance-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var table = new PlayerRows().Capture()!;
            var report = new UnitySourceEmissionReport { SourceGeneration = "generated" };
            UnityV29AssemblyReferenceProvenance.AddToReport(report, [table.CreateReport("Synthetic.Application", 0)]);
            Assert.DoesNotThrow(() => UnityCsOutputFormat.EnsureCompleteSourceGeneration(report));
            Assert.That(report.DeclarationFidelity, Is.EqualTo("partial"));
            Assert.That(report.DeclarationDiagnostics.Single(), Does.StartWith("DECL006:"));
            report.WriteJson(path);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var item = json.RootElement.GetProperty("AssemblyReferenceMetadata")[0];
            Assert.That(item.GetProperty("IdentityBasis").GetString(), Is.EqualTo("ResolvedPlayerAssemblyDefinition"));
            Assert.That(item.GetProperty("OriginalManagedAssemblyRefIdentity").GetString(), Is.EqualTo("UnavailableInPlayerSchema"));
            Assert.That(item.GetProperty("ResolvedReferences").GetArrayLength(), Is.EqualTo(3));
            UnityV29AssemblyReferenceProvenance.AddToReport(report,
            [new("Synthetic.Invalid", null, null, UnityAssemblyReferenceBindingStatus.InvalidPlayerBindings, [], "invalid index")]);
            Assert.That(report.SourceGeneration, Is.EqualTo("partial"));
            Assert.That(report.Diagnostics.Single(), Does.StartWith("SOURCE019:"));
            Assert.Throws<InvalidOperationException>(() => UnityCsOutputFormat.EnsureCompleteSourceGeneration(report));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void OriginalPlayerScopeAuthenticatesResolvedReferencesAndRejectsCachedIdentityMutation()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_ENUM_ARGUMENT_INVOCATION_FIXTURE");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("Configure the neutral enum argument player input.");
        Cpp2IlApi.ResetInternalState();
        try
        {
            TestGameLoader.EnsureInit();
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var selected = new[] { "NativeEnumArgumentInvocationFixture" };
            var assessment = UnityV29AssemblyReferenceProvenance.Analyze(app, selected).Single();
            Assert.That(assessment.BindingStatus, Is.EqualTo(UnityAssemblyReferenceBindingStatus.AuthenticatedResolvedIdentities));
            Assert.That(assessment.ResolvedReferences, Is.Not.Empty);
            app.AssembliesByName.Add("Synthetic.Alias", app.GetAssemblyByName(selected[0])!);
            try
            {
                Assert.That(UnityV29AssemblyReferenceProvenance.Analyze(app, ["Synthetic.Alias"]).Single().BindingStatus,
                    Is.EqualTo(UnityAssemblyReferenceBindingStatus.InvalidPlayerBindings));
            }
            finally { app.AssembliesByName.Remove("Synthetic.Alias"); }
            app.AssembliesByName.Add("Synthetic.MissingContext", null!);
            try
            {
                var invalid = UnityV29AssemblyReferenceProvenance.Analyze(app, ["Synthetic.MissingContext"]).Single();
                Assert.That(invalid.BindingStatus, Is.EqualTo(UnityAssemblyReferenceBindingStatus.InvalidPlayerBindings));
                Assert.That(invalid.ResolvedReferences, Is.Empty);
                var strict = new UnitySourceEmissionReport { SourceGeneration = "generated" };
                UnityV29AssemblyReferenceProvenance.AddToReport(strict, [invalid]);
                Assert.Throws<InvalidOperationException>(() => UnityCsOutputFormat.EnsureCompleteSourceGeneration(strict));
            }
            finally { app.AssembliesByName.Remove("Synthetic.MissingContext"); }
            var original = app.Metadata.AssemblyDefinitions[assessment.ResolvedReferences[0].ResolvedAssemblyDefinitionOrdinal].AssemblyName;
            var previous = original.publicKeyToken;
            try
            {
                original.publicKeyToken ^= 1;
                var rejected = UnityV29AssemblyReferenceProvenance.Analyze(app, selected).Single();
                Assert.That(rejected.BindingStatus, Is.EqualTo(UnityAssemblyReferenceBindingStatus.InvalidPlayerBindings));
                Assert.That(rejected.ResolvedReferences, Is.Empty);
            }
            finally { original.publicKeyToken = previous; }
            var restored = UnityV29AssemblyReferenceProvenance.Analyze(app, selected).Single();
            Assert.That(restored.BindingStatus, Is.EqualTo(assessment.BindingStatus));
            Assert.That(restored.SourceAssemblyDefinitionOrdinal, Is.EqualTo(assessment.SourceAssemblyDefinitionOrdinal));
            Assert.That(restored.ResolvedReferences, Is.EqualTo(assessment.ResolvedReferences));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
