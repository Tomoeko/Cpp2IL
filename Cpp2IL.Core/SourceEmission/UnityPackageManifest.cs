using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// An explicitly supplied Unity project manifest. The player cannot establish package
/// identities or versions, so no metadata-derived package entries are added here.
/// </summary>
internal sealed class UnityPackageManifest
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly byte[] EmptyBytes = Encoding.UTF8.GetBytes("{\"dependencies\":{}}\n");
    private static readonly Regex PackageName = new("^[a-z0-9_]+(?:[.-][a-z0-9_]+)*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex RegistryVersion = new(
        "^(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal byte[] Bytes { get; }
    internal string Provenance { get; }
    internal int DependencyCount { get; }

    private UnityPackageManifest(byte[] bytes, string provenance, int dependencyCount)
    {
        Bytes = bytes;
        Provenance = provenance;
        DependencyCount = dependencyCount;
    }

    internal static UnityPackageManifest Load(string? path)
    {
        if (path == null)
            return new UnityPackageManifest(EmptyBytes, "default-empty", 0);
        if (string.IsNullOrWhiteSpace(path))
            throw Invalid();

        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumBytes)
                throw Invalid();
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw Invalid();
        }
        if (bytes.Length is <= 0 or > MaximumBytes)
            throw Invalid();

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            return new UnityPackageManifest(bytes, "explicit-auxiliary", Validate(document.RootElement));
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static int Validate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? dependencies = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw Invalid();
            switch (property.Name)
            {
                case "dependencies":
                    dependencies = ValidateDependencies(property.Value);
                    break;
                case "scopedRegistries":
                    ValidateRegistries(property.Value);
                    break;
                case "testables":
                    ValidatePackageNames(property.Value);
                    break;
                case "registry":
                    if (!SafeHttpsUrl(property.Value, gitDependency: false))
                        throw Invalid();
                    break;
                case "enableLockFile":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw Invalid();
                    break;
                case "resolutionStrategy":
                    if (property.Value.ValueKind != JsonValueKind.String ||
                        property.Value.GetString() is not ("lowest" or "highestPatch" or "highestMinor" or "highest"))
                        throw Invalid();
                    break;
                default:
                    // Unknown fields could carry project-local paths or credentials.
                    throw Invalid();
            }
        }
        // Unity does not load packages or its Package Manager from an empty manifest.
        if (seen.Count == 0)
            throw Invalid();
        return dependencies ?? 0;
    }

    private static int ValidateDependencies(JsonElement dependencies)
    {
        if (dependencies.ValueKind != JsonValueKind.Object)
            throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in dependencies.EnumerateObject())
        {
            if (!SafePackageName(property.Name) || !seen.Add(property.Name) ||
                property.Value.ValueKind != JsonValueKind.String)
                throw Invalid();
            var value = property.Value.GetString()!;
            if (!SafeRegistryVersion(value) && !SafeHttpsUrl(property.Value, gitDependency: true))
                throw Invalid();
        }
        return seen.Count;
    }

    private static void ValidateRegistries(JsonElement registries)
    {
        if (registries.ValueKind != JsonValueKind.Array || registries.GetArrayLength() > 64)
            throw Invalid();
        foreach (var registry in registries.EnumerateArray())
        {
            if (registry.ValueKind != JsonValueKind.Object)
                throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in registry.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw Invalid();
                switch (property.Name)
                {
                    case "name":
                        if (!SafeLabel(property.Value)) throw Invalid();
                        break;
                    case "url":
                        if (!SafeHttpsUrl(property.Value, gitDependency: false)) throw Invalid();
                        break;
                    case "scopes":
                        ValidatePackageNames(property.Value);
                        break;
                    default:
                        throw Invalid();
                }
            }
            if (!seen.SetEquals(["name", "url", "scopes"]))
                throw Invalid();
        }
    }

    private static void ValidatePackageNames(JsonElement names)
    {
        if (names.ValueKind != JsonValueKind.Array || names.GetArrayLength() > 4096)
            throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names.EnumerateArray())
            if (name.ValueKind != JsonValueKind.String ||
                !SafePackageName(name.GetString()!) || !seen.Add(name.GetString()!))
                throw Invalid();
    }

    private static bool SafePackageName(string value)
    {
        return value.Length is >= 3 and <= 214 && value.Contains('.') && PackageName.IsMatch(value);
    }

    private static bool SafeRegistryVersion(string value)
    {
        if (value.Length is < 5 or > 128)
            return false;
        var match = RegistryVersion.Match(value);
        if (!match.Success)
            return false;

        // SemVer permits leading zeroes in build metadata, but not in numeric
        // prerelease identifiers (for example, 1.0.0-preview.01 is invalid).
        var prerelease = match.Groups["prerelease"];
        return !prerelease.Success || prerelease.Value.Split('.').All(identifier =>
            identifier.Length == 1 || identifier[0] != '0' ||
            !identifier.All(character => character is >= '0' and <= '9'));
    }

    private static bool SafeHttpsUrl(JsonElement element, bool gitDependency)
    {
        if (element.ValueKind != JsonValueKind.String)
            return false;
        var value = element.GetString()!;
        var gitPrefix = value.StartsWith("git+https://", StringComparison.Ordinal);
        var address = gitPrefix ? value.Substring(4) : value;
        if (value.Length is < 9 or > 2048 || value.Any(character =>
                character is <= ' ' or >= '\u007f' or '\\') ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!gitDependency)
            return uri.Query.Length == 0 && uri.Fragment.Length == 0;
        if ((!gitPrefix && !uri.AbsolutePath.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) ||
            !SafeGitSubfolder(uri.Query))
            return false;
        return uri.Fragment.Length == 0 || (uri.Fragment.Length > 1 &&
            uri.Fragment.Substring(1).All(character => AsciiAlphanumeric(character) ||
                character is '.' or '-' or '_' or '/'));
    }

    // Unity's leading slash in ?path=/a/b is relative to the Git repository
    // root, not a host filesystem absolute path. Accept only plain, bounded
    // segments so encoding, traversal, and additional URL parameters cannot
    // turn an auxiliary dependency into a local path or concealed directive.
    private static bool SafeGitSubfolder(string query)
    {
        if (query.Length == 0)
            return true;
        if (query.Length > 512 || !query.StartsWith("?path=/", StringComparison.Ordinal))
            return false;
        var path = query.Substring(7);
        var segments = path.Split('/');
        return segments.All(segment => segment.Length is >= 1 and <= 128 &&
            segment is not ("." or "..") &&
            segment.All(character => AsciiAlphanumeric(character) || character is '.' or '_' or '-'));
    }

    private static bool SafeLabel(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            return false;
        var value = element.GetString()!;
        return value.Length is >= 1 and <= 128 &&
            value.All(character => character is >= ' ' and <= '~' && character != '\\') &&
            value[0] is not ('/' or '~') && !value.Contains(":") &&
            value.IndexOf("..", StringComparison.Ordinal) < 0;
    }

    private static bool AsciiAlphanumeric(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static ArgumentException Invalid() =>
        new("Unity package manifest must be a bounded JSON project manifest with explicit safe package dependencies. Local paths, URL user info, and unsupported fields are rejected.");
}
