using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AsmResolver;
using AsmResolver.DotNet;
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

    public static UnitySourceEmissionReport Emit(IEnumerable<AssemblyDefinition> assemblies, IEnumerable<string> selectedAssemblyNames,
        IEnumerable<string> referenceDirectories, string outputDirectory)
    {
        var selected = selectedAssemblyNames.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("Unity source output requires an explicit, nonempty application assembly selection.");
        if (selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new ArgumentException("Selected assembly names collide on case-insensitive filesystems.");
        var byName = assemblies.ToDictionary(a => a.Name!.ToString(), StringComparer.Ordinal);
        foreach (var name in selected)
        {
            ExplicitAssemblyResolver.ValidateSimpleName(name);
            if (IsTargetProvidedAssembly(name))
                throw new ArgumentException($"{name} must be a target reference, not regenerated application source.");
            if (!byName.ContainsKey(name))
                throw new ArgumentException($"Selected application assembly {name} was not found in the recovered metadata.");
        }

        // A fresh directory prevents old source or editor assemblies from masking failed output.
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException("Unity source output requires an empty output directory.");
        Directory.CreateDirectory(outputDirectory);

        var report = new UnitySourceEmissionReport();
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
                    foreach (var parameter in method.ParameterDefinitions.Where(p => p.HasFieldMarshal && p.MarshalDescriptor == null))
                        report.Diagnostics.Add($"SOURCE004: {name}: {method.FullName}: Parameter {parameter.Sequence} marshaling metadata is unresolved; no MarshalAs value was invented.");
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
                var sourceReferences = references.Where(r => selected.Contains(r, StringComparer.Ordinal)).ToList();
                var externalReferences = references.Except(sourceReferences, StringComparer.Ordinal).ToList();
                if (!IsPredefinedAssembly(name) && sourceReferences.Any(IsPredefinedAssembly))
                    throw new NotSupportedException("Unity assembly definitions cannot reference predefined assemblies. The original assembly boundary requires explicit project configuration.");

                using var file = new PEFile(Path.Combine(managedDirectory, name + ".dll"));
                resolver.ValidateReferenceClosure(file);
                var decompiler = new CSharpDecompiler(file, resolver, CreateSettings());
                decompiler.ILTransforms.Add(new CollectWarnings(report.Diagnostics, name));
                var source = decompiler.DecompileWholeModuleAsString();
                var relativeDirectory = name == "Assembly-CSharp-firstpass"
                    ? "Assets/Plugins/Recovered/" + name
                    : "Assets/Recovered/" + name;
                var sourceDirectory = Path.Combine(outputDirectory, relativeDirectory);
                Directory.CreateDirectory(sourceDirectory);
                File.WriteAllText(Path.Combine(sourceDirectory, "Recovered.cs"),
                    "// Generated from recovered IL. Compilation and behavioral equivalence require independent validation.\n" + source, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(sourceDirectory, "csc.rsp"), "-langversion:9.0\n-unsafe\n-checked-\n", new UTF8Encoding(false));

                if (!IsPredefinedAssembly(name))
                {
                    var json = "{\n  \"name\":" + JsonText.Quote(name) + ",\n  \"references\":" + JsonText.Array(sourceReferences) +
                        ",\n  \"allowUnsafeCode\":true,\n  \"overrideReferences\":true,\n  \"precompiledReferences\":" +
                        JsonText.Array(externalReferences.Where(r => !IsTargetProvidedAssembly(r)).Select(r => r + ".dll")) +
                        ",\n  \"autoReferenced\":true\n}\n";
                    File.WriteAllText(Path.Combine(sourceDirectory, name + ".asmdef"), json, new UTF8Encoding(false));
                }

                report.Assemblies.Add(new UnitySourceAssemblyReport
                {
                    Name = name,
                    SourceFile = relativeDirectory + "/Recovered.cs",
                    SourceReferences = sourceReferences,
                    ExternalReferences = externalReferences,
                });
            }

            // Framework and Unity assemblies stay external. Other explicit references must be
            // configured as plugins/packages by the validation harness; do not copy oracle DLLs.
            Directory.CreateDirectory(Path.Combine(outputDirectory, "ProjectSettings"));
            File.WriteAllText(Path.Combine(outputDirectory, "ProjectSettings", "ProjectVersion.txt"), $"m_EditorVersion: {TargetUnityVersion}\n");
            Directory.CreateDirectory(Path.Combine(outputDirectory, "Packages"));
            File.WriteAllText(Path.Combine(outputDirectory, "Packages", "manifest.json"), "{\"dependencies\":{}}\n");
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

    public static bool IsTargetProvidedAssembly(string name) => name is "mscorlib" or "netstandard" or "System" or "UnityEngine" or "UnityEditor" or "Microsoft.CSharp" ||
        name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
        name.StartsWith("UnityEditor.", StringComparison.Ordinal) || name.StartsWith("Unity.", StringComparison.Ordinal);

    private static bool IsPredefinedAssembly(string name) => name is "Assembly-CSharp" or "Assembly-CSharp-firstpass";

    private sealed class CollectWarnings(List<string> diagnostics, string assemblyName) : IILTransform
    {
        public void Run(ILFunction function, ILTransformContext context)
        {
            foreach (var warning in function.Warnings)
                diagnostics.Add($"SOURCE002: {assemblyName}: {function.Method?.FullName}: {warning}");
        }
    }

}
