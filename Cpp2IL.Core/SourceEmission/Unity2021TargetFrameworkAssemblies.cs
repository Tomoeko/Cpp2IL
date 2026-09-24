using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Framework identities in the Unity 2021.3.35f1 NET_Unity_4_8 Windows target profile.
/// A familiar simple name alone does not establish that the target supplies a reference.
/// </summary>
internal static class Unity2021TargetFrameworkAssemblies
{
    private static readonly Version FrameworkVersion = new(4, 0, 0, 0);
    private static readonly byte[] FrameworkToken = [0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89];
    private static readonly byte[] FacadeToken = [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a];
    private static readonly byte[] NetstandardToken = [0xcc, 0x7b, 0x13, 0xff, 0xcd, 0x2d, 0xdd, 0x51];

    private static readonly IReadOnlyDictionary<string, (Version Version, byte[] Token)> Identities =
        new Dictionary<string, (Version, byte[])>(StringComparer.Ordinal)
        {
            ["mscorlib"] = (FrameworkVersion, FrameworkToken),
            ["System"] = (FrameworkVersion, FrameworkToken),
            ["System.Core"] = (FrameworkVersion, FrameworkToken),
            ["System.Xml"] = (FrameworkVersion, FrameworkToken),
            ["System.Xml.Linq"] = (FrameworkVersion, FrameworkToken),
            ["System.Numerics"] = (FrameworkVersion, FrameworkToken),
            ["Microsoft.CSharp"] = (FrameworkVersion, FacadeToken),
            ["netstandard"] = (new Version(2, 1, 0, 0), NetstandardToken),
            ["System.Runtime"] = (new Version(4, 1, 2, 0), FacadeToken),
            ["System.Collections"] = (new Version(4, 0, 11, 0), FacadeToken),
        };

    internal static bool IsKnownName(string name) => Identities.ContainsKey(name);

    internal static bool HasTargetIdentity(AssemblyReference reference) =>
        reference.Name?.ToString() is { } name && Identities.TryGetValue(name, out var identity) &&
        reference.Version == identity.Version &&
        string.IsNullOrEmpty(reference.Culture?.ToString()) &&
        (reference.PublicKeyOrToken ?? []).SequenceEqual(identity.Token);
}
