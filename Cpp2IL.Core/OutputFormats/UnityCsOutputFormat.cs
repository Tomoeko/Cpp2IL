using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.ProcessingLayers;
using Cpp2IL.Core.SourceEmission;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.OutputFormats;

public sealed class UnityCsOutputFormat : Cpp2IlOutputFormat
{
    public override string OutputFormatId => "cs_unity";
    public override string OutputFormatName => "Unity 2021.3 C# project (requires independent compilation and behavior validation)";

    public IReadOnlyList<string>? AssemblyNames { get; set; }
    public IReadOnlyList<string>? ReferenceDirectories { get; set; }
    public string? PackageManifestPath { get; set; }
    public string? ExternalReferenceMapPath { get; set; }
    public bool RequireCompleteRecovery { get; set; }

    public override void DoOutput(ApplicationAnalysisContext context, string outputRoot)
    {
        var selected = (AssemblyNames ?? Cpp2IlApi.RuntimeOptions?.UnitySourceAssemblies ?? []).ToArray();
        var references = ReferenceDirectories ?? Cpp2IlApi.RuntimeOptions?.UnityReferenceDirectories ?? [];
        var packageManifest = PackageManifestPath ?? Cpp2IlApi.RuntimeOptions?.UnityPackageManifestPath;
        var externalReferenceMap = ExternalReferenceMapPath ?? Cpp2IlApi.RuntimeOptions?.UnityExternalReferenceMapPath;
        var strict = RequireCompleteRecovery || (Cpp2IlApi.RuntimeOptions?.StrictRecovery ?? false);
        if (selected.Length == 0)
            throw new ArgumentException("Specify --unity-source-assemblies with the exact application assembly names to regenerate.");
        if (context.UnityVersion.ToString() != UnitySourceProjectEmitter.TargetUnityVersion)
            throw new NotSupportedException($"Unity source output currently requires Unity {UnitySourceProjectEmitter.TargetUnityVersion} inputs.");
        if (context.Binary is not PE || context.Binary.InstructionSetId != DefaultInstructionSets.X86_64 || context.Binary.PointerSizeBytes != 8)
            throw new NotSupportedException("Unity source output currently requires Windows x64 PE player inputs.");
        var projectDirectory = Path.Combine(outputRoot, "UnityProject");
        if (Directory.Exists(projectDirectory) && Directory.EnumerateFileSystemEntries(projectDirectory).Any())
            throw new IOException("Unity source output requires an empty project output directory.");

        Directory.CreateDirectory(outputRoot);
        // A source export must not silently omit custom attributes merely because the caller
        // did not also select an optional diagnostic processing layer. Analysis is cached.
        new AttributeAnalysisProcessingLayer().Process(context);
        var recovery = new AsmResolverDllOutputFormatIlRecovery();
        List<AssemblyDefinition> assemblies;
        try
        {
            assemblies = recovery.BuildAssemblies(context);
        }
        finally
        {
            recovery.LastRecoveryReport?.WriteJson(Path.Combine(outputRoot, "source-recovery-report.json"));
        }
        var report = recovery.LastRecoveryReport ?? throw new InvalidOperationException("IL recovery did not produce its required disposition report.");
        if (strict)
            report.EnsureComplete(selected);

        var sourceReport = UnitySourceProjectEmitter.Emit(assemblies, selected, references, projectDirectory, packageManifest,
            externalReferenceMap, context.MetadataVersion);
        sourceReport.RecoveryReportFile = "../source-recovery-report.json";
        if (report.Methods.Any(m => selected.Contains(m.AssemblyName, StringComparer.Ordinal) && m.IsUnresolved))
        {
            sourceReport.SourceGeneration = "partial";
            sourceReport.Diagnostics.Add("SOURCE003: Selected application methods contain unresolved recovery. See the recovery report; fallback bodies are not recovered behavior.");
        }
        sourceReport.WriteJson(Path.Combine(projectDirectory, "source-emission-report.json"));
        if (strict && sourceReport.SourceGeneration != "generated")
            throw new InvalidOperationException("Strict Unity source output rejected decompiler diagnostics. See the source emission report.");

        Logger.InfoNewline("Unity source project generated. Method dispositions are in source-recovery-report.json. Exact-editor compilation, native rebuilding, and behavioral validation remain unverified.", "UnitySource");
    }
}
