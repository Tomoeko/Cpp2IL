using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Explicit Unity compilation kinds for managed references outside regenerated source.
/// Assembly metadata cannot distinguish an assembly definition from a precompiled plug-in.
/// </summary>
internal sealed class UnityExternalReferenceMap
{
    private const int MaximumBytes = 1024 * 1024;
    private readonly Dictionary<string, UnityExternalReferenceEntry> _entries;

    internal string Provenance { get; }

    private UnityExternalReferenceMap(Dictionary<string, UnityExternalReferenceEntry> entries, string provenance)
    {
        _entries = entries;
        Provenance = provenance;
    }

    internal static UnityExternalReferenceMap Load(string? path)
    {
        if (path == null)
            return new UnityExternalReferenceMap(new Dictionary<string, UnityExternalReferenceEntry>(StringComparer.Ordinal), "none");
        if (string.IsNullOrWhiteSpace(path))
            throw Invalid();

        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumBytes)
                throw Invalid();
            bytes = File.ReadAllBytes(path);
            if (bytes.Length is <= 0 or > MaximumBytes)
                throw Invalid();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw Invalid();
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            return new UnityExternalReferenceMap(Parse(document.RootElement), "explicit-auxiliary");
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    internal bool TryGet(string assemblyName, out UnityExternalReferenceEntry entry) =>
        _entries.TryGetValue(assemblyName, out entry!);

    private static Dictionary<string, UnityExternalReferenceEntry> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Invalid();
        JsonElement references = default;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name) || property.Name != "references")
                throw Invalid();
            references = property.Value;
        }
        if (!seen.Contains("references") || references.ValueKind != JsonValueKind.Array || references.GetArrayLength() > 4096)
            throw Invalid();

        var result = new Dictionary<string, UnityExternalReferenceEntry>(StringComparer.Ordinal);
        foreach (var item in references.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw Invalid();
            string? assembly = null;
            string? kind = null;
            bool? autoReferenced = null;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!fields.Add(property.Name))
                    throw Invalid();
                switch (property.Name)
                {
                    case "assembly":
                        if (property.Value.ValueKind != JsonValueKind.String)
                            throw Invalid();
                        assembly = property.Value.GetString();
                        break;
                    case "kind":
                        if (property.Value.ValueKind != JsonValueKind.String)
                            throw Invalid();
                        kind = property.Value.GetString();
                        break;
                    case "autoReferenced":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw Invalid();
                        autoReferenced = property.Value.GetBoolean();
                        break;
                    default:
                        throw Invalid();
                }
            }

            if (assembly == null || kind == null || assembly.Length > 256 ||
                kind is not ("asmdef" or "precompiled-plugin" or "target-provided") ||
                kind == "target-provided" && autoReferenced != null)
                throw Invalid();
            try
            {
                ExplicitAssemblyResolver.ValidateSimpleName(assembly);
            }
            catch (ArgumentException)
            {
                throw Invalid();
            }
            if (result.ContainsKey(assembly))
                throw Invalid();
            result.Add(assembly, new UnityExternalReferenceEntry(kind, autoReferenced));
        }
        return result;
    }

    private static ArgumentException Invalid() =>
        new("Unity external reference map must be bounded JSON with unique safe assembly names and explicit asmdef, precompiled-plugin, or target-provided kinds.");
}

internal sealed class UnityExternalReferenceEntry(string kind, bool? autoReferenced)
{
    internal string Kind { get; } = kind;
    internal bool? AutoReferenced { get; } = autoReferenced;
}
