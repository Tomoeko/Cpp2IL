using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ICSharpCode.Decompiler.Metadata;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Resolves only recovered application assemblies and explicitly supplied target references.
/// Deliberately has no host runtime, GAC, NuGet, or working-directory fallback.
/// </summary>
public sealed class ExplicitAssemblyResolver : IAssemblyResolver, IDisposable
{
    private readonly Dictionary<string, string> _recovered;
    private readonly string[] _directories;
    private readonly Dictionary<string, PEFile> _files = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ExplicitAssemblyResolver(IEnumerable<string> recoveredAssemblies, IEnumerable<string> referenceDirectories)
    {
        _recovered = recoveredAssemblies.ToDictionary(p => AssemblyName.GetAssemblyName(p).Name!, Path.GetFullPath, StringComparer.Ordinal);
        _directories = referenceDirectories.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var directory in _directories)
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("A configured target reference directory does not exist.");
        }
    }

    public MetadataFile? Resolve(IAssemblyReference reference)
    {
        lock (_lock)
        {
            ValidateSimpleName(reference.Name);
            if (_recovered.TryGetValue(reference.Name, out var recovered))
                return LoadMatching(recovered, reference);

            // Reject ambiguous references instead of allowing search order to choose a target API.
            var candidates = _directories.Select(d => Path.Combine(d, reference.Name + ".dll")).Where(File.Exists).ToArray();
            if (candidates.Length > 1)
                throw new InvalidOperationException($"Multiple explicit references were supplied for {reference.Name}.");

            return candidates.Length == 0 ? null : LoadMatching(candidates[0], reference);
        }
    }

    public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName)
    {
        // Unity player managed assemblies are single-module. Do not probe beside arbitrary inputs.
        throw new NotSupportedException("Multi-module assemblies are not supported by Unity source output.");
    }

    public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));

    public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));

    public void ValidateReferenceClosure(MetadataFile module)
    {
        var pending = new Queue<MetadataFile>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(module);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var handle in current.Metadata.AssemblyReferences)
            {
                var reference = new ICSharpCode.Decompiler.Metadata.AssemblyReference(current, handle);
                if (!visited.Add(reference.FullName))
                    continue;
                // Decompilers can reconstruct primitive types without resolving their assembly.
                // Validate metadata references eagerly so that this is never mistaken for a
                // complete target reference set.
                var resolved = Resolve(reference) ?? throw new ResolutionException(reference, null, null);
                pending.Enqueue(resolved);
            }
        }
    }

    private PEFile LoadMatching(string path, IAssemblyReference reference)
    {
        var identity = AssemblyName.GetAssemblyName(path);
        if (identity.Name != reference.Name || identity.Version != reference.Version ||
            (identity.CultureName ?? "") != (reference.Culture ?? "") ||
            !(identity.GetPublicKeyToken() ?? []).SequenceEqual(reference.PublicKeyToken ?? []))
            throw new InvalidOperationException($"Explicit reference identity does not match {reference.FullName}.");

        if (!_files.TryGetValue(path, out var file))
            _files.Add(path, file = new PEFile(path));
        return file;
    }

    internal static void ValidateSimpleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal) ||
            name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("An assembly name cannot be represented safely as a portable project path.");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')
            throw new ArgumentException("An assembly name is a reserved Windows device path.");
    }

    public void Dispose()
    {
        foreach (var file in _files.Values)
            file.Dispose();
        _files.Clear();
    }
}
