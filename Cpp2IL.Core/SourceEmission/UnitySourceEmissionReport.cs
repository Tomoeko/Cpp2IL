using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cpp2IL.Core.Reporting;

namespace Cpp2IL.Core.SourceEmission;

public sealed class UnitySourceEmissionReport
{
    public int SchemaVersion => 2;
    public string UnityVersion { get; set; } = "2021.3.35f1";
    public string SourceDialect => "C# 9 with Unity restrictions";
    public string SourceGeneration { get; set; } = "incomplete";
    public string UnityCompilation => "unverified";
    public string DeclarationFidelity => "unverified";
    public string WindowsNativeBuild => "unverified";
    public string BehavioralValidation => "unverified";
    public string ScriptAssetBindings => "unverified";
    public string PackageManifestProvenance { get; set; } = "default-empty";
    public int PackageDependencyCount { get; set; }
    public string ExternalReferenceMapProvenance { get; set; } = "none";
    public string? RecoveryReportFile { get; set; }
    public List<UnitySourceAssemblyReport> Assemblies { get; } = [];
    public List<string> Diagnostics { get; } = [];

    public void WriteJson(string path)
    {
        var json = "{\n" +
            "  \"SchemaVersion\":2,\n" +
            "  \"UnityVersion\":" + JsonText.Quote(UnityVersion) + ",\n" +
            "  \"SourceDialect\":" + JsonText.Quote(SourceDialect) + ",\n" +
            "  \"SourceGeneration\":" + JsonText.Quote(SourceGeneration) + ",\n" +
            "  \"DeclarationFidelity\":" + JsonText.Quote(DeclarationFidelity) + ",\n" +
            "  \"UnityCompilation\":" + JsonText.Quote(UnityCompilation) + ",\n" +
            "  \"WindowsNativeBuild\":" + JsonText.Quote(WindowsNativeBuild) + ",\n" +
            "  \"BehavioralValidation\":" + JsonText.Quote(BehavioralValidation) + ",\n" +
            "  \"ScriptAssetBindings\":" + JsonText.Quote(ScriptAssetBindings) + ",\n" +
            "  \"PackageManifestProvenance\":" + JsonText.Quote(PackageManifestProvenance) + ",\n" +
            "  \"PackageDependencyCount\":" + PackageDependencyCount + ",\n" +
            "  \"ExternalReferenceMapProvenance\":" + JsonText.Quote(ExternalReferenceMapProvenance) + ",\n" +
            "  \"ReferencePolicy\":\"Explicit target references and external compilation kinds; recovered selected application assemblies; no host fallback\",\n" +
            "  \"RecoveryReportFile\":" + JsonText.Quote(RecoveryReportFile) + ",\n" +
            "  \"Assemblies\":[" + string.Join(",", Assemblies.Select(a =>
                "{\"Name\":" + JsonText.Quote(a.Name) + ",\"SourceFile\":" + JsonText.Quote(a.SourceFile) +
                ",\"SourceFiles\":" + JsonText.Array(a.SourceFiles) + ",\"ComponentScripts\":[" +
                string.Join(",", a.ComponentScripts.Select(c => "{\"TypeName\":" + JsonText.Quote(c.TypeName) +
                    ",\"SourceFile\":" + JsonText.Quote(c.SourceFile) + ",\"EditorDiscovery\":\"unverified\"}")) + "]" +
                ",\"SourceReferences\":" + JsonText.Array(a.SourceReferences) + ",\"ExternalReferences\":" + JsonText.Array(a.ExternalReferences) +
                ",\"ExternalReferenceKinds\":[" + string.Join(",", a.ExternalReferenceKinds.Select(reference =>
                    "{\"Name\":" + JsonText.Quote(reference.Name) + ",\"Kind\":" + JsonText.Quote(reference.Kind) +
                    ",\"Provenance\":" + JsonText.Quote(reference.Provenance) + "}")) + "]}")) + "],\n" +
            "  \"Diagnostics\":" + JsonText.Array(Diagnostics) + "\n}\n";
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }
}

public sealed class UnitySourceAssemblyReport
{
    public string Name { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public List<string> SourceFiles { get; set; } = [];
    public List<UnityComponentScriptReport> ComponentScripts { get; set; } = [];
    public List<string> SourceReferences { get; set; } = [];
    public List<string> ExternalReferences { get; set; } = [];
    public List<UnityExternalReferenceReport> ExternalReferenceKinds { get; set; } = [];
}

public sealed class UnityExternalReferenceReport
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "unclassified";
    public string Provenance { get; set; } = "none";
}

public sealed class UnityComponentScriptReport
{
    public string TypeName { get; set; } = "";
    public string SourceFile { get; set; } = "";
}
