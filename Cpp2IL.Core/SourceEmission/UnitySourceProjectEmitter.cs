using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Reporting;
using Cpp2IL.Core.Utils.AsmResolver;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.Metadata;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Emits a code-only Unity project from recovered managed assemblies. Source generation is
/// distinct from IL validity, Unity compilation, native rebuilding, and behavioral validation.
/// </summary>
public static class UnitySourceProjectEmitter
{
    public const string TargetUnityVersion = "2021.3.35f1";
    // Unity's NET_Unity_4_8 profile supplies this exact framework identity.
    private static readonly Version TargetSystemNumericsVersion = new(4, 0, 0, 0);
    private static readonly byte[] TargetSystemNumericsToken = [0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89];

    public static DecompilerSettings CreateSettings() => new(LanguageVersion.CSharp9_0)
    {
        FileScopedNamespaces = false,
        InitAccessors = false,
        CovariantReturns = false,
        RecordClasses = false,
        WithExpressions = false,
        UsePrimaryConstructorSyntax = false,
        NativeIntegers = false,
        UseDebugSymbols = false,
        ShowXmlDocumentation = false,
        ThrowOnAssemblyResolveErrors = true,
        RemoveDeadCode = false,
        RemoveDeadStores = false,
    };

    /// <param name="playerMetadataVersion">The metadata version of player-derived assemblies. Leave unset for authored managed inputs.</param>
    public static UnitySourceEmissionReport Emit(IEnumerable<AssemblyDefinition> assemblies, IEnumerable<string> selectedAssemblyNames,
        IEnumerable<string> referenceDirectories, string outputDirectory, string? packageManifestPath = null,
        string? externalReferenceMapPath = null, float? playerMetadataVersion = null)
    {
        var packageManifest = UnityPackageManifest.Load(packageManifestPath);
        var externalReferenceMap = UnityExternalReferenceMap.Load(externalReferenceMapPath);
        var selected = selectedAssemblyNames.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("Unity source output requires an explicit, nonempty application assembly selection.");
        if (selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new ArgumentException("Selected assembly names collide on case-insensitive filesystems.");
        var byName = assemblies.ToDictionary(a => a.Name!.ToString(), StringComparer.Ordinal);
        foreach (var name in selected)
        {
            ExplicitAssemblyResolver.ValidateSimpleName(name);
            if (IsReservedSourceAssembly(name))
                throw new ArgumentException($"{name} must be a target reference, not regenerated application source.");
            if (!byName.ContainsKey(name))
                throw new ArgumentException($"Selected application assembly {name} was not found in the recovered metadata.");
        }
        if (selected.Select(AssemblyDirectoryName).Select(n => n.Normalize(NormalizationForm.FormC))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new ArgumentException("Selected assembly output directories collide after escaping Unity folder names.");

        // A fresh directory prevents old source or editor assemblies from masking failed output.
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException("Unity source output requires an empty output directory.");
        Directory.CreateDirectory(outputDirectory);

        var report = new UnitySourceEmissionReport();
        report.PackageManifestProvenance = packageManifest.Provenance;
        report.PackageDependencyCount = packageManifest.DependencyCount;
        report.ExternalReferenceMapProvenance = externalReferenceMap.Provenance;
        var externalIdentityByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflictingExternalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var managedDirectory = Path.Combine(outputDirectory, "RecoveredManaged");
            Directory.CreateDirectory(managedDirectory);
            var managedPaths = new List<string>();
            foreach (var name in selected)
            {
                var assembly = byName[name];
                if (assembly.PublicKey?.Any() == true)
                    report.Diagnostics.Add($"SOURCE005: {name}: Signing configuration is unavailable. Generated source does not establish preservation of strong-name identity.");
                foreach (var type in assembly.Modules.SelectMany(m => m.GetAllTypes()))
                {
                    foreach (var field in type.Fields.Where(f => f.HasFieldMarshal && f.MarshalDescriptor == null))
                        report.Diagnostics.Add($"SOURCE004: {name}: {field.FullName}: Field marshaling metadata is unresolved; no MarshalAs value was invented.");
                    foreach (var method in type.Methods)
                    {
                        foreach (var parameter in method.ParameterDefinitions.Where(p => p.HasFieldMarshal && p.MarshalDescriptor == null))
                            report.Diagnostics.Add($"SOURCE004: {name}: {method.FullName}: Parameter {parameter.Sequence} marshaling metadata is unresolved; no MarshalAs value was invented.");
                        // V29 player metadata retains the byref type but not the return signature's
                        // readonly modifier. A known managed modifier remains represented by a wrapper.
                        if (playerMetadataVersion == 29f && method.Signature?.ReturnType is ByReferenceTypeSignature)
                            report.Diagnostics.Add($"SOURCE008: {name}: {method.FullName}: Unity 2021.3 metadata does not distinguish ref from ref readonly returns; return mutability is unresolved.");
                    }
                }
                var path = Path.Combine(managedDirectory, name + ".dll");
                using (var stream = File.Create(path))
                    byName[name].WriteManifest(stream, RecoveredAssemblyImageBuilder.Create(ThrowErrorListener.Instance));
                managedPaths.Add(path);
            }

            using var resolver = new ExplicitAssemblyResolver(managedPaths, referenceDirectories);
            foreach (var name in selected)
            {
                var module = byName[name].ManifestModule!;
                var references = module.AssemblyReferences.Select(r => r.Name!.ToString()).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                var referencesByName = module.AssemblyReferences.GroupBy(r => r.Name!.ToString(), StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                if (name is "Assembly-CSharp-Editor" or "Assembly-CSharp-Editor-firstpass" ||
                    references.Any(reference => reference is "UnityEditor" ||
                        reference.StartsWith("UnityEditor.", StringComparison.Ordinal)))
                    report.Diagnostics.Add($"SOURCE009: {name}: Editor-only source placement and platform restriction are unresolved; the generated asmdef cannot establish a player-buildable project.");
                var sourceReferences = references.Where(r => selected.Contains(r, StringComparer.Ordinal)).ToList();
                var externalReferences = references.Except(sourceReferences, StringComparer.Ordinal).ToList();
                foreach (var reference in module.AssemblyReferences)
                {
                    var referenceName = reference.Name!.ToString();
                    // Identity-qualified target references still need cross-assembly conflict checks.
                    if (selected.Contains(referenceName, StringComparer.Ordinal) || IsTargetProvidedAssembly(referenceName))
                        continue;
                    var identity = reference.FullName;
                    if (externalIdentityByName.TryGetValue(referenceName, out var previousIdentity))
                    {
                        if (previousIdentity != identity)
                            conflictingExternalNames.Add(referenceName);
                    }
                    else
                        externalIdentityByName.Add(referenceName, identity);
                }
                var assemblyDefinitionReferences = new List<string>();
                var precompiledReferences = new List<string>();
                var referenceKinds = new List<UnityExternalReferenceReport>();
                foreach (var reference in externalReferences)
                {
                    var configured = externalReferenceMap.TryGet(reference, out var entry);
                    if (referencesByName[reference].All(IsTargetProvidedAssembly))
                    {
                        if (configured && entry.Kind != "target-provided")
                            report.Diagnostics.Add($"SOURCE007: {name}: {reference}: A known target-provided assembly cannot be classified as an asmdef or plug-in.");
                        referenceKinds.Add(new UnityExternalReferenceReport
                        {
                            Name = reference, Kind = "target-provided",
                            Provenance = IsTargetProvidedAssembly(reference) ? "known-target-name" : "known-target-identity",
                        });
                        continue;
                    }

                    if (!configured)
                    {
                        report.Diagnostics.Add($"SOURCE007: {name}: {reference}: External reference kind is unresolved; supply an explicit Unity reference map.");
                        referenceKinds.Add(new UnityExternalReferenceReport { Name = reference, Kind = "unclassified", Provenance = "none" });
                        continue;
                    }

                    referenceKinds.Add(new UnityExternalReferenceReport { Name = reference, Kind = entry.Kind, Provenance = "explicit-auxiliary" });
                    if (entry.Kind == "asmdef")
                        assemblyDefinitionReferences.Add(reference);
                    else if (entry.Kind == "precompiled-plugin")
                        precompiledReferences.Add(reference + ".dll");

                    if (IsPredefinedAssembly(name) && entry.Kind != "target-provided" && entry.AutoReferenced != true)
                        report.Diagnostics.Add($"SOURCE007: {name}: {reference}: Predefined assemblies require autoReferenced=true for this external dependency in the explicit reference map.");
                }
                if (!IsPredefinedAssembly(name) && sourceReferences.Any(IsPredefinedAssembly))
                    throw new NotSupportedException("Unity assembly definitions cannot reference predefined assemblies. The original assembly boundary requires explicit project configuration.");

                using var file = new PEFile(Path.Combine(managedDirectory, name + ".dll"));
                resolver.ValidateReferenceClosure(file);
                var settings = CreateSettings();
                var decompiler = new CSharpDecompiler(file, resolver, settings);
                decompiler.ILTransforms.Add(new CollectWarnings(report.Diagnostics, name));
                var sources = UnityComponentSourceLayout.Split(decompiler.DecompileWholeModuleAsSingleFile(), settings, name, report.Diagnostics);
                var relativeDirectory = name == "Assembly-CSharp-firstpass"
                    ? "Assets/Plugins/Recovered/" + name
                    : "Assets/Recovered/" + AssemblyDirectoryName(name);
                var sourceDirectory = Path.Combine(outputDirectory, relativeDirectory);
                Directory.CreateDirectory(sourceDirectory);
                foreach (var source in sources)
                {
                    var sourcePath = Path.Combine(sourceDirectory, source.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                    File.WriteAllText(sourcePath,
                        "// Generated from recovered IL. Compilation and behavioral equivalence require independent validation.\n" + source.Text, new UTF8Encoding(false));
                }
                File.WriteAllText(Path.Combine(sourceDirectory, "csc.rsp"), "-langversion:9.0\n-unsafe\n-checked-\n", new UTF8Encoding(false));

                if (!IsPredefinedAssembly(name))
                {
                    var json = "{\n  \"name\":" + JsonText.Quote(name) + ",\n  \"references\":" +
                        JsonText.Array(sourceReferences.Concat(assemblyDefinitionReferences).OrderBy(r => r, StringComparer.Ordinal)) +
                        ",\n  \"allowUnsafeCode\":true,\n  \"overrideReferences\":true,\n  \"precompiledReferences\":" +
                        JsonText.Array(precompiledReferences) +
                        ",\n  \"autoReferenced\":true\n}\n";
                    var definitionFile = name.StartsWith(".", StringComparison.Ordinal) ? "AssemblyDefinition.asmdef" : name + ".asmdef";
                    File.WriteAllText(Path.Combine(sourceDirectory, definitionFile), json, new UTF8Encoding(false));
                }

                report.Assemblies.Add(new UnitySourceAssemblyReport
                {
                    Name = name,
                    SourceFile = relativeDirectory + "/Recovered.cs",
                    SourceFiles = sources.Select(s => relativeDirectory + "/" + s.Path).ToList(),
                    ComponentScripts = sources.Where(s => s.ComponentType != null).Select(s => new UnityComponentScriptReport
                    {
                        TypeName = s.ComponentType!, SourceFile = relativeDirectory + "/" + s.Path,
                    }).ToList(),
                    SourceReferences = sourceReferences,
                    ExternalReferences = externalReferences,
                    ExternalReferenceKinds = referenceKinds,
                });
            }

            foreach (var referenceName in conflictingExternalNames.OrderBy(value => value, StringComparer.Ordinal))
                report.Diagnostics.Add($"SOURCE007: {referenceName}: Distinct managed identities share one Unity assembly name across selected assemblies.");

            // Framework and Unity assemblies stay external. Other explicit references must be
            // configured as plugins/packages by the validation harness; do not copy oracle DLLs.
            Directory.CreateDirectory(Path.Combine(outputDirectory, "ProjectSettings"));
            File.WriteAllText(Path.Combine(outputDirectory, "ProjectSettings", "ProjectVersion.txt"), $"m_EditorVersion: {TargetUnityVersion}\n");
            Directory.CreateDirectory(Path.Combine(outputDirectory, "Packages"));
            File.WriteAllBytes(Path.Combine(outputDirectory, "Packages", "manifest.json"), packageManifest.Bytes);
            File.WriteAllText(Path.Combine(outputDirectory, "Assets", "csc.rsp"), "-langversion:9.0\n-unsafe\n-checked-\n");
            report.SourceGeneration = report.Diagnostics.Count == 0 ? "generated" : "partial";
        }
        catch
        {
            report.SourceGeneration = "failed";
            report.Diagnostics.Add("SOURCE001: Source generation failed; no compilation or recovery claim is established. See the local command log.");
            throw;
        }
        finally
        {
            report.WriteJson(Path.Combine(outputDirectory, "source-emission-report.json"));
        }
        return report;
    }

    public static bool IsTargetProvidedAssembly(string name) =>
        name is "mscorlib" or "netstandard" or "System" or "System.Core" or "System.Runtime" or "System.Private.CoreLib" or
            "System.Collections" or "System.Xml" or "System.Xml.Linq" or "Microsoft.CSharp" or "UnityEngine" or "UnityEditor" ||
        name.StartsWith("UnityEngine.", StringComparison.Ordinal) && name.EndsWith("Module", StringComparison.Ordinal) ||
        name.StartsWith("UnityEditor.", StringComparison.Ordinal) && name.EndsWith("Module", StringComparison.Ordinal);

    internal static bool IsTargetProvidedAssembly(AsmResolver.DotNet.AssemblyReference reference) =>
        IsTargetProvidedAssembly(reference.Name?.ToString() ?? "") ||
        reference.Name?.ToString() == "System.Numerics" &&
        reference.Version == TargetSystemNumericsVersion &&
        string.IsNullOrEmpty(reference.Culture?.ToString()) &&
        (reference.PublicKeyOrToken ?? []).SequenceEqual(TargetSystemNumericsToken);

    private static bool IsReservedSourceAssembly(string name) => IsTargetProvidedAssembly(name) ||
        name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
        name.StartsWith("UnityEditor.", StringComparison.Ordinal) || name.StartsWith("Unity.", StringComparison.Ordinal);

    private static bool IsPredefinedAssembly(string name) => name is "Assembly-CSharp" or "Assembly-CSharp-firstpass";

    private static string AssemblyDirectoryName(string name)
    {
        // Names are metadata identities, not instructions to select Unity import/compile rules.
        // Keep established ordinary paths and the intentional firstpass location unchanged.
        var reserved = name.ToUpperInvariant() is "ASSETS" or "EDITOR" or "EDITOR DEFAULT RESOURCES" or "GIZMOS" or
            "PLUGINS" or "RESOURCES" or "STANDARD ASSETS" or "PRO STANDARD ASSETS" or "STREAMINGASSETS" or "CVS";
        return reserved || name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal) ||
            name.EndsWith(".androidlib", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".androidpack", StringComparison.OrdinalIgnoreCase)
            ? "assembly-" + name + "-source" : name;
    }

    private sealed class CollectWarnings(List<string> diagnostics, string assemblyName) : IILTransform
    {
        public void Run(ILFunction function, ILTransformContext context)
        {
            foreach (var warning in function.Warnings)
                diagnostics.Add($"SOURCE002: {assemblyName}: {function.Method?.FullName}: {warning}");
        }
    }

}
