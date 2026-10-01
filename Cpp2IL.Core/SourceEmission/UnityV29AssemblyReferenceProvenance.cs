using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// V29 dependency rows contain indices into assembly definitions, rather than
/// the original managed AssemblyRef identity rows. Authenticate the resolved
/// identities without substituting an assumed compiler reference identity.
/// </summary>
public static class UnityV29AssemblyReferenceProvenance
{
    internal const string DeclarationDiagnostic =
        "DECL006: Version-29 player metadata retains resolved dependency AssemblyDefinition identities, not the original managed AssemblyRef identity rows. Original per-reference version, key/token, flags, hash and row provenance remain unavailable from these player inputs. Validate those declaration facts separately with an independent managed oracle.";

    // The matching V29 GlobalMetadataFileInternals.h header has 64 int32 fields.
    private const int HeaderBytes = 256;
    private const int AssemblyRowBytes = 64;
    private const int StringSectionHeader = 24;
    private const int AssemblySectionHeader = 176;
    private const int ReferenceSectionHeader = 192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<UnityAssemblyReferenceAssemblyReport> Analyze(
        ApplicationAnalysisContext context, IReadOnlyCollection<string> selectedAssemblyNames)
    {
        if (!UnityV29ReturnMetadataProvenance.IsV29Family(context.MetadataVersion)) return [];
        var metadata = context.Metadata;
        var table = Capture(context.MetadataVersion, metadata.Length, metadata.ReadByteArrayAtRawAddress,
            metadata.metadataHeader, metadata.AssemblyDefinitions, metadata.referencedAssemblies);
        var result = new List<UnityAssemblyReferenceAssemblyReport>();
        foreach (var name in selectedAssemblyNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            if (!context.AssembliesByName.TryGetValue(name, out var assembly))
                throw new ArgumentException($"Selected assembly is absent from the analyzed player: {name}", nameof(selectedAssemblyNames));
            if (assembly == null)
            {
                result.Add(Unavailable(name, UnityAssemblyReferenceBindingStatus.InvalidPlayerBindings,
                    "selected assembly context is missing from the analysis cache"));
                continue;
            }
            if (assembly.Definition == null)
            {
                result.Add(Unavailable(name, UnityAssemblyReferenceBindingStatus.UnavailablePlayerDefinition,
                    "selected assembly has no original player definition"));
                continue;
            }
            try
            {
                if (assembly.Name != name || table == null || !TryBinding(context, assembly, table, out var ordinal) ||
                    table.ReferenceOrdinals(ordinal).Any(index => !TryBinding(context,
                        context.ResolveContextForAssembly(table.Rows[index].Original), table, out var bound) || bound != index))
                {
                    result.Add(Unavailable(name, UnityAssemblyReferenceBindingStatus.InvalidPlayerBindings,
                        "raw header, reference indices, definition rows or analysis identity disagree"));
                    continue;
                }
                result.Add(table.CreateReport(name, ordinal));
            }
            catch (Exception exception) when (InvalidInput(exception))
            {
                result.Add(Unavailable(name, UnityAssemblyReferenceBindingStatus.InvalidPlayerBindings,
                    "resolved player assembly identity could not be read"));
            }
        }
        // The report is a fresh observation, not a reusable admission proof.
        // Reject changes between raw capture and context identity validation.
        if (table != null && !table.Matches(Capture(context.MetadataVersion, metadata.Length,
                metadata.ReadByteArrayAtRawAddress, metadata.metadataHeader, metadata.AssemblyDefinitions, metadata.referencedAssemblies)))
            return result.Select(item => Unavailable(item.Name, UnityAssemblyReferenceBindingStatus.InvalidPlayerBindings,
                "player reference input changed while its provenance was recorded")).ToArray();
        return result;
    }

    public static void AddToReport(UnitySourceEmissionReport report,
        IReadOnlyList<UnityAssemblyReferenceAssemblyReport> references)
    {
        report.AssemblyReferenceMetadata.AddRange(references);
        if (references.Count == 0) return;
        report.DeclarationFidelity = "partial";
        report.DeclarationDiagnostics.Add(DeclarationDiagnostic);
        foreach (var item in references.Where(item => item.BindingStatus != UnityAssemblyReferenceBindingStatus.AuthenticatedResolvedIdentities))
        {
            if (report.SourceGeneration == "generated") report.SourceGeneration = "partial";
            report.Diagnostics.Add($"SOURCE019: {item.Name}: Resolved player assembly references could not be authenticated ({item.FailureReason}); their identities must not be guessed.");
        }
    }

    private static UnityAssemblyReferenceAssemblyReport Unavailable(string name,
        UnityAssemblyReferenceBindingStatus status, string reason) => new(name, null, null, status, [], reason);

    private static bool TryBinding(ApplicationAnalysisContext context, AssemblyAnalysisContext? assembly,
        Table table, out int ordinal)
    {
        ordinal = -1;
        if (assembly?.Definition is not { } definition || !ReferenceEquals(assembly.AppContext, context) ||
            context.Assemblies.Count(candidate => ReferenceEquals(candidate, assembly)) != 1) return false;
        for (var index = 0; index < table.Rows.Count; index++)
            if (ReferenceEquals(table.Rows[index].Original, definition))
            {
                if (ordinal != -1) return false;
                ordinal = index;
            }
        if (ordinal < 0) return false;
        var row = table.Rows[ordinal];
        if (!ReferenceEquals(definition.AssemblyName, row.OriginalName) ||
            context.Metadata.imageDefinitions is not { } images ||
            definition.ImageIndex < 0 || definition.ImageIndex >= images.Length || images[definition.ImageIndex] == null) return false;
        var expectedVersion = row.Build < 0 ? new Version(0, 0, 0, 0) : new Version(row.Major, row.Minor, row.Build, row.Revision);
        return context.Assemblies.Count(candidate => ReferenceEquals(candidate?.Definition, definition)) == 1 &&
            context.AssembliesByName.TryGetValue(row.Name, out var byName) && ReferenceEquals(byName, assembly) &&
            ReferenceEquals(context.ResolveContextForAssembly(definition), assembly) &&
            assembly.Name == row.Name && (assembly.Culture ?? "") == row.Culture && assembly.Version == expectedVersion &&
            assembly.Flags == row.Flags && assembly.HashAlgorithm == row.HashAlgorithm &&
            (assembly.PublicKey ?? []).AsSpan().SequenceEqual(row.PublicKey) &&
            (assembly.PublicKeyToken ?? []).AsSpan().SequenceEqual(row.PublicKeyToken);
    }

    internal sealed class Row
    {
        internal Il2CppAssemblyDefinition Original { get; }
        internal Il2CppAssemblyNameDefinition OriginalName { get; }
        internal int ReferenceStart { get; }
        internal int ReferenceCount { get; }
        internal string Name { get; }
        internal string Culture { get; }
        internal uint Flags { get; }
        internal uint HashAlgorithm { get; }
        internal int HashLength { get; }
        internal int Major { get; }
        internal int Minor { get; }
        internal int Build { get; }
        internal int Revision { get; }
        private readonly byte[] _publicKey;
        private readonly byte[] _publicKeyToken;
        internal ReadOnlySpan<byte> PublicKey => _publicKey;
        internal ReadOnlySpan<byte> PublicKeyToken => _publicKeyToken;

        internal Row(Il2CppAssemblyDefinition original, ReadOnlySpan<byte> raw, byte[] strings)
        {
            Original = original;
            OriginalName = original.AssemblyName;
            ReferenceStart = Int(raw, 8);
            ReferenceCount = Int(raw, 12);
            Name = ReadString(strings, Int(raw, 16));
            Culture = ReadString(strings, Int(raw, 20));
            _publicKey = ReadKey(strings, Int(raw, 24));
            HashAlgorithm = UInt(raw, 28);
            HashLength = Int(raw, 32);
            Flags = UInt(raw, 36);
            Major = Int(raw, 40);
            Minor = Int(raw, 44);
            Build = Int(raw, 48);
            Revision = Int(raw, 52);
            _publicKeyToken = UInt64(raw, 56) == 0 ? [] : raw.Slice(56, 8).ToArray();
        }

        internal UnityResolvedAssemblyIdentityReport Identity => new(Name, Culture,
            $"{Major}.{Minor}.{Build}.{Revision}", Flags, HashAlgorithm, HashLength,
            BitConverter.ToString(_publicKey).Replace("-", ""), BitConverter.ToString(_publicKeyToken).Replace("-", ""));
    }

    internal sealed class Table
    {
        private readonly byte[][] _bytes;
        private readonly int[] _references;
        internal IReadOnlyList<Row> Rows { get; }

        internal Table(byte[] header, byte[] assemblies, byte[] references, byte[] strings, Row[] rows, int[] indices)
        {
            _bytes = [header, assemblies, references, strings];
            _references = indices;
            Rows = Array.AsReadOnly(rows);
        }

        internal IEnumerable<int> ReferenceOrdinals(int ordinal) =>
            _references.Skip(Rows[ordinal].ReferenceStart < 0 ? 0 : Rows[ordinal].ReferenceStart).Take(Rows[ordinal].ReferenceCount);

        internal UnityAssemblyReferenceAssemblyReport CreateReport(string name, int ordinal)
        {
            var row = Rows[ordinal];
            var entries = ReferenceOrdinals(ordinal).Select((target, index) => new UnityAssemblyReferenceReport(
                index, row.ReferenceStart + index, target, Rows[target].Identity)).ToArray();
            return new(name, ordinal, row.ReferenceStart, UnityAssemblyReferenceBindingStatus.AuthenticatedResolvedIdentities,
                Array.AsReadOnly(entries), null);
        }

        internal bool Matches(Table? other) => other != null && Rows.Count == other.Rows.Count &&
            _bytes.Select((bytes, index) => bytes.AsSpan().SequenceEqual(other._bytes[index])).All(equal => equal) &&
            Rows.Select((row, index) => ReferenceEquals(row.Original, other.Rows[index].Original) &&
                ReferenceEquals(row.OriginalName, other.Rows[index].OriginalName)).All(equal => equal);
    }

    // This entry point is shared by the real metadata reader and neutral raw-row
    // controls. It never resolves names through mutable string caches.
    internal static Table? Capture(float version, long length, Func<long, int, byte[]> read,
        Il2CppGlobalMetadataHeader? cachedHeader, Il2CppAssemblyDefinition[]? definitions, int[]? references)
    {
        try
        {
            if (!UnityV29ReturnMetadataProvenance.IsV29Family(version) || length < HeaderBytes ||
                cachedHeader == null || definitions == null || references == null) return null;
            var header = ReadExact(read, 0, HeaderBytes);
            if (UInt(header, 0) != Il2CppMetadata.MetadataMagic || Int(header, 4) != 29 ||
                cachedHeader.magicNumber != Il2CppMetadata.MetadataMagic || cachedHeader.version != 29) return null;
            for (var offset = 8; offset < HeaderBytes; offset += 8)
            {
                var start = Int(header, offset);
                var size = Int(header, offset + 4);
                if (start < 0 || size < 0 || (size != 0 && start < HeaderBytes) || start > length || size > length - start) return null;
            }
            if (!SectionMatches(header, StringSectionHeader, cachedHeader.@string) ||
                !SectionMatches(header, AssemblySectionHeader, cachedHeader.assemblies) ||
                !SectionMatches(header, ReferenceSectionHeader, cachedHeader.referencedAssemblies)) return null;
            var stringOffset = Int(header, StringSectionHeader);
            var stringSize = Int(header, StringSectionHeader + 4);
            var assemblyOffset = Int(header, AssemblySectionHeader);
            var assemblySize = Int(header, AssemblySectionHeader + 4);
            var referenceOffset = Int(header, ReferenceSectionHeader);
            var referenceSize = Int(header, ReferenceSectionHeader + 4);
            if (assemblySize % AssemblyRowBytes != 0 || referenceSize % sizeof(int) != 0 ||
                (assemblyOffset & 3) != 0 || (referenceOffset & 3) != 0 ||
                definitions.Length != assemblySize / AssemblyRowBytes || references.Length != referenceSize / sizeof(int) ||
                definitions.Distinct().Count() != definitions.Length ||
                Overlap(stringOffset, stringSize, assemblyOffset, assemblySize) ||
                Overlap(stringOffset, stringSize, referenceOffset, referenceSize) ||
                Overlap(assemblyOffset, assemblySize, referenceOffset, referenceSize)) return null;
            var assemblies = ReadExact(read, assemblyOffset, assemblySize);
            var rawReferences = ReadExact(read, referenceOffset, referenceSize);
            var strings = ReadExact(read, stringOffset, stringSize);
            var indices = new int[references.Length];
            for (var index = 0; index < indices.Length; index++)
            {
                indices[index] = Int(rawReferences, index * sizeof(int));
                if (indices[index] != references[index] || indices[index] < 0 || indices[index] >= definitions.Length) return null;
            }
            var rows = new Row[definitions.Length];
            for (var ordinal = 0; ordinal < rows.Length; ordinal++)
            {
                var raw = assemblies.AsSpan(ordinal * AssemblyRowBytes, AssemblyRowBytes);
                var definition = definitions[ordinal];
                if (!RowMatches(raw, definition)) return null;
                rows[ordinal] = new(definition, raw, strings);
                var start = rows[ordinal].ReferenceStart;
                var count = rows[ordinal].ReferenceCount;
                if (count < 0 || (start < 0 ? count != 0 : start > indices.Length || count > indices.Length - start)) return null;
            }
            return new(header, assemblies, rawReferences, strings, rows, indices);
        }
        catch (Exception exception) when (InvalidInput(exception)) { return null; }
    }

    private static bool RowMatches(ReadOnlySpan<byte> raw, Il2CppAssemblyDefinition? definition) =>
        definition?.AssemblyName is { } name && definition.ImageIndex == Int(raw, 0) && definition.Token == UInt(raw, 4) &&
        definition.ReferencedAssemblyStart == Int(raw, 8) && definition.ReferencedAssemblyCount == Int(raw, 12) &&
        name.nameIndex == Int(raw, 16) && name.cultureIndex == Int(raw, 20) && name.publicKeyIndex == Int(raw, 24) &&
        name.hash_alg == UInt(raw, 28) && name.hash_len == Int(raw, 32) && name.flags == UInt(raw, 36) &&
        name.major == Int(raw, 40) && name.minor == Int(raw, 44) && name.build == Int(raw, 48) &&
        name.revision == Int(raw, 52) && name.publicKeyToken == UInt64(raw, 56);

    private static byte[] ReadExact(Func<long, int, byte[]> read, int offset, int count)
    {
        var bytes = read(offset, count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return (byte[])bytes.Clone();
    }

    private static string ReadString(byte[] bytes, int index)
    {
        if (index < 0 || index >= bytes.Length) throw new InvalidDataException();
        var end = Array.IndexOf(bytes, (byte)0, index);
        if (end < 0) throw new InvalidDataException();
        return StrictUtf8.GetString(bytes, index, end - index);
    }

    private static byte[] ReadKey(byte[] strings, int index)
    {
        if (index < 0 || index >= strings.Length) throw new InvalidDataException();
        using var stream = new MemoryStream(strings, false);
        stream.Position = index;
        var count = stream.ReadUnityCompressedUint();
        if (count > stream.Length - stream.Position) throw new InvalidDataException();
        var result = new byte[checked((int)count)];
        if (stream.Read(result, 0, result.Length) != result.Length) throw new EndOfStreamException();
        return result;
    }

    private static bool SectionMatches(byte[] header, int offset, Il2CppGlobalMetadataSectionHeader? section) =>
        section != null && section.Offset == Int(header, offset) && section.Size == Int(header, offset + 4);
    private static bool Overlap(int left, int leftSize, int right, int rightSize) =>
        leftSize != 0 && rightSize != 0 && (long)left < (long)right + rightSize && (long)right < (long)left + leftSize;
    private static int Int(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, sizeof(int)));
    private static uint UInt(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));
    private static ulong UInt64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, sizeof(ulong)));
    private static bool InvalidInput(Exception exception) => exception is ArgumentException or InvalidOperationException or
        IndexOutOfRangeException or KeyNotFoundException or OverflowException or IOException or InvalidDataException;
}
