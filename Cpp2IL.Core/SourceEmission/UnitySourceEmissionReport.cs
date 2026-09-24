using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cpp2IL.Core.Reporting;

namespace Cpp2IL.Core.SourceEmission;

public sealed class UnitySourceEmissionReport
{
    public int SchemaVersion => 4;
    public string UnityVersion { get; set; } = "2021.3.35f1";
    public string SourceDialect => "C# 9 with Unity restrictions";
    public string SourceGeneration { get; set; } = "incomplete";
    public string UnityCompilation => "unverified";
    public string DeclarationFidelity { get; set; } = "unverified";
    public string WindowsNativeBuild => "unverified";
    public string BehavioralValidation => "unverified";
    public string ScriptAssetBindings => "unverified";
    public string PackageManifestProvenance { get; set; } = "default-empty";
    public int PackageDependencyCount { get; set; }
    public string ExternalReferenceMapProvenance { get; set; } = "none";
    public string? RecoveryReportFile { get; set; }
    public List<UnitySourceAssemblyReport> Assemblies { get; } = [];
    public List<string> Diagnostics { get; } = [];
    public List<UnityReturnMetadataAssemblyReport> ReturnMetadata { get; } = [];
    public List<UnityValueTypeClassLayoutAssemblyReport> ValueTypeClassLayoutMetadata { get; } = [];
    public List<string> DeclarationDiagnostics { get; } = [];

    public void WriteJson(string path)
    {
        var json = "{\n" +
            "  \"SchemaVersion\":" + SchemaVersion + ",\n" +
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
            "  \"ReturnMetadata\":[" + string.Join(",", ReturnMetadata.Select(item =>
                "{\"Name\":" + JsonText.Quote(item.Name) + ",\"PlayerMethodCount\":" + item.PlayerMethodCount +
                ",\"UnknownReturnRowCount\":" + item.UnknownReturnRowCount +
                ",\"UnknownReturnCustomAttributeCount\":" + item.UnknownReturnCustomAttributeCount +
                ",\"EvidenceSource\":" + JsonText.Quote(item.EvidenceSource) +
                ",\"ReturnRowPresence\":" + JsonText.Quote(item.ReturnRowPresence.ToString()) +
                ",\"ReturnCustomAttributes\":" + JsonText.Quote(item.ReturnCustomAttributes.ToString()) + "}")) + "],\n" +
            "  \"ValueTypeClassLayoutMetadata\":[" + string.Join(",", ValueTypeClassLayoutMetadata.Select(item =>
                "{\"Name\":" + JsonText.Quote(item.Name) +
                ",\"UnknownDeclaredClassSizeCount\":" + item.UnknownDeclaredClassSizeCount +
                ",\"EvidenceSource\":" + JsonText.Quote(item.EvidenceSource) +
                ",\"Types\":[" + string.Join(",", item.Types.Select(type =>
                    "{\"Name\":" + JsonText.Quote(type.Name) +
                    ",\"RawNativeSize\":" + type.RawNativeSize +
                    ",\"EmittedClassSizeCandidate\":" + type.EmittedClassSizeCandidate +
                    ",\"DeclaredClassSize\":" + JsonText.Quote(type.DeclaredClassSize) + "}")) + "]}")) + "],\n" +
            "  \"DeclarationDiagnostics\":" + JsonText.Array(DeclarationDiagnostics) + ",\n" +
            "  \"Diagnostics\":" + JsonText.Array(Diagnostics) + "\n}\n";
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }
}

public enum UnityReturnMetadataAvailability
{
    UnknownInPlayer,
    KnownAbsent,
    KnownPresent,
}

public sealed record UnityReturnMetadataAssemblyReport(
    string Name,
    int PlayerMethodCount,
    int UnknownReturnRowCount,
    int UnknownReturnCustomAttributeCount,
    UnityReturnMetadataAvailability ReturnRowPresence,
    UnityReturnMetadataAvailability ReturnCustomAttributes)
{
    public string EvidenceSource => "v29-player-metadata";
}

public sealed record UnityValueTypeClassLayoutAssemblyReport(string Name, IReadOnlyList<UnityValueTypeClassLayoutTypeReport> Types)
{
    public int UnknownDeclaredClassSizeCount => Types.Count;
    public string EvidenceSource => "v29-player-type-and-native-size";
}

public sealed record UnityValueTypeClassLayoutTypeReport(string Name, int RawNativeSize)
{
    // Match the existing DLL writer's conversion. -1 becomes zero; zero remains zero.
    public uint EmittedClassSizeCandidate => RawNativeSize == -1 ? 0 : unchecked((uint)RawNativeSize);
    public string DeclaredClassSize => "UnknownDeclaredClassSize";
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
