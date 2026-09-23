using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cpp2IL.Core.Reporting;

namespace Cpp2IL.Core.SourceEmission;

public sealed class UnitySourceEmissionReport
{
    public int SchemaVersion => 1;
    public string UnityVersion { get; set; } = "2021.3.35f1";
    public string SourceDialect => "C# 9 with Unity restrictions";
    public string SourceGeneration { get; set; } = "incomplete";
    public string UnityCompilation => "unverified";
    public string WindowsNativeBuild => "unverified";
    public string BehavioralValidation => "unverified";
    public string ScriptAssetBindings => "unverified";
    public string? RecoveryReportFile { get; set; }
    public List<UnitySourceAssemblyReport> Assemblies { get; } = [];
    public List<string> Diagnostics { get; } = [];

    public void WriteJson(string path)
    {
        var json = "{\n" +
            "  \"SchemaVersion\":1,\n" +
            "  \"UnityVersion\":" + JsonText.Quote(UnityVersion) + ",\n" +
            "  \"SourceDialect\":" + JsonText.Quote(SourceDialect) + ",\n" +
            "  \"SourceGeneration\":" + JsonText.Quote(SourceGeneration) + ",\n" +
            "  \"UnityCompilation\":" + JsonText.Quote(UnityCompilation) + ",\n" +
            "  \"WindowsNativeBuild\":" + JsonText.Quote(WindowsNativeBuild) + ",\n" +
            "  \"BehavioralValidation\":" + JsonText.Quote(BehavioralValidation) + ",\n" +
            "  \"ScriptAssetBindings\":" + JsonText.Quote(ScriptAssetBindings) + ",\n" +
            "  \"ReferencePolicy\":\"Explicit target references; recovered selected application assemblies; no host fallback\",\n" +
            "  \"RecoveryReportFile\":" + JsonText.Quote(RecoveryReportFile) + ",\n" +
            "  \"Assemblies\":[" + string.Join(",", Assemblies.Select(a =>
                "{\"Name\":" + JsonText.Quote(a.Name) + ",\"SourceFile\":" + JsonText.Quote(a.SourceFile) +
                ",\"SourceReferences\":" + JsonText.Array(a.SourceReferences) + ",\"ExternalReferences\":" + JsonText.Array(a.ExternalReferences) + "}")) + "],\n" +
            "  \"Diagnostics\":" + JsonText.Array(Diagnostics) + "\n}\n";
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }
}

public sealed class UnitySourceAssemblyReport
{
    public string Name { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public List<string> SourceReferences { get; set; } = [];
    public List<string> ExternalReferences { get; set; } = [];
}
