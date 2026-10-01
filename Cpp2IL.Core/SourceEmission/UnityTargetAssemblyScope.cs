using System;
using System.Linq;
using System.Security.Cryptography;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.SourceEmission;

internal enum UnityTargetAssemblyKind
{
    Application,
    TargetReference,
    UnresolvedTargetReference,
}

/// <summary>
/// Separates supplied target identities from application and package assemblies.
/// A namespace-like assembly prefix establishes neither target availability nor
/// the Unity project configuration needed for an external dependency.
/// </summary>
internal static class UnityTargetAssemblyScope
{
    internal const string UnresolvedIdentityReason =
        "SCOPE001: A known target assembly name has an unqualified managed identity; " +
        "reference availability is unresolved and application source must not replace it.";

    internal static bool UsesExactTargetProfile(ApplicationAnalysisContext app) =>
        app.UnityVersion.ToString() == UnitySourceProjectEmitter.TargetUnityVersion &&
        app.MetadataVersion == 29 && app.Binary.InstructionSetId == DefaultInstructionSets.X86_64 &&
        app.Binary is PE { PointerSizeBytes: sizeof(ulong) };

    internal static UnityTargetAssemblyKind Classify(AssemblyAnalysisContext assembly)
    {
        var originalName = assembly.DefaultName;
        var currentName = assembly.Name;
        if (!KnownName(originalName) && !KnownName(currentName))
            return UnityTargetAssemblyKind.Application;
        if (originalName != currentName || assembly.Definition == null ||
            assembly.Flags != assembly.DefaultFlags || assembly.HashAlgorithm != assembly.DefaultHashAlgorithm)
            return UnityTargetAssemblyKind.UnresolvedTargetReference;
        var original = new Identity(assembly.DefaultName, assembly.DefaultVersion, assembly.DefaultCulture,
            assembly.DefaultPublicKey, assembly.DefaultPublicKeyToken);
        var current = new Identity(assembly.Name, assembly.Version, assembly.Culture,
            assembly.PublicKey, assembly.PublicKeyToken);
        return ClassifyOriginalIdentity(original, current);
    }

    internal readonly record struct Identity(string Name, Version? Version, string? Culture,
        byte[]? PublicKey, byte[]? PublicKeyToken);

    internal static UnityTargetAssemblyKind ClassifyOriginalIdentity(Identity original, Identity current)
    {
        if (!KnownName(original.Name) && !KnownName(current.Name))
            return UnityTargetAssemblyKind.Application;
        // Identity overrides cannot hide an application assembly as a target
        // reference or rename a known target into regenerated application source.
        if (current.Name != original.Name || current.Version != original.Version ||
            (current.Culture ?? "") != (original.Culture ?? "") ||
            !(current.PublicKey ?? []).SequenceEqual(original.PublicKey ?? []) ||
            !(current.PublicKeyToken ?? []).SequenceEqual(original.PublicKeyToken ?? []))
            return UnityTargetAssemblyKind.UnresolvedTargetReference;
        return ClassifyIdentity(original.Name, original.Version, original.Culture,
            original.PublicKey, original.PublicKeyToken);
    }

    internal static UnityTargetAssemblyKind ClassifyIdentity(string name, Version? version, string? culture,
        byte[]? publicKey, byte[]? publicKeyToken)
    {
        var token = publicKeyToken;
        if (publicKey is { Length: > 0 } key)
        {
            if (!TryNormalizePublicKey(key, true, out var fromKey) ||
                token is { Length: > 0 } && !token.SequenceEqual(fromKey))
                return KnownName(name) ? UnityTargetAssemblyKind.UnresolvedTargetReference : UnityTargetAssemblyKind.Application;
            token = fromKey;
        }
        return Classify(name, version, culture, token);
    }

    internal static UnityTargetAssemblyKind Classify(string name, Version? version, string? culture,
        byte[]? publicKeyOrToken, bool isFullPublicKey = false)
    {
        if (!KnownName(name)) return UnityTargetAssemblyKind.Application;
        if (!TryNormalizePublicKey(publicKeyOrToken, isFullPublicKey, out var token))
            return UnityTargetAssemblyKind.UnresolvedTargetReference;
        return Unity2021TargetAssemblies.HasTargetIdentity(name, version, culture, token) ||
               Unity2021TargetFrameworkAssemblies.HasTargetIdentity(name, version, culture, token)
            ? UnityTargetAssemblyKind.TargetReference : UnityTargetAssemblyKind.UnresolvedTargetReference;
    }

    // Even a mismatched known target identity must not be replaced by regenerated
    // framework source. Unknown prefixed names remain ordinary application names.
    internal static bool IsReservedSourceName(string name) => KnownName(name) || name == "System.Private.CoreLib";

    private static bool KnownName(string name) =>
        Unity2021TargetAssemblies.IsKnownName(name) || Unity2021TargetFrameworkAssemblies.IsKnownName(name);

    internal static bool TryNormalizePublicKey(byte[]? value, bool isFullPublicKey, out byte[] token)
    {
        token = [];
        if (!isFullPublicKey)
        {
            if (value == null || value.Length == 0) return true;
            if (value.Length != 8) return false;
            token = (byte[])value.Clone();
            return true;
        }
        if (value == null || value.Length is < 16 or > 16384) return false;
        using var hash = SHA1.Create();
        var digest = hash.ComputeHash(value);
        token = digest.AsSpan(digest.Length - 8).ToArray();
        Array.Reverse(token);
        return true;
    }
}
