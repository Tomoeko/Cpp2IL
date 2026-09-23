"""Mutation checks for the read-only declaration comparison boundary."""

import json
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import shutil
import subprocess
import tempfile
from xml.sax.saxutils import escape


ROOT = Path(__file__).resolve().parent.parent
COMPARER = ROOT / "Validation/DeclarationComparer/DeclarationComparer.csproj"
DLL = COMPARER.parent / "bin/Release/net10.0/DeclarationComparer.dll"
SOURCE = """
using System;
using System.Runtime.InteropServices;
public sealed class MarkerAttribute : Attribute
{
    public MarkerAttribute(int number) { }
}
public sealed class ChoiceAttribute : Attribute
{
    public ChoiceAttribute(object value) { }
    public ChoiceAttribute(string value) { }
}
[StructLayout(LayoutKind.Explicit, Pack = 4, Size = 16)]
public struct Record
{
    [FieldOffset(0), MarshalAs(UnmanagedType.U1)] public bool Enabled;
}
public sealed class Container<T> where T : class { public T Item; }
[Choice((object)"fixture")]
public sealed class Cases
{
    public const int Limit = 31;
    [Marker(7)]
    public int Sum(ref int value, int extra = 3) { return value + extra; }
}
"""
PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework>
<AssemblyName>DeclarationFixtureTest</AssemblyName><DebugType>none</DebugType>
<Nullable>disable</Nullable><GenerateAssemblyInfo>false</GenerateAssemblyInfo>
</PropertyGroup></Project>
"""


def run(command, expected=0):
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=120)
    if result.returncode != expected:
        raise AssertionError("Unexpected process result:\n" + result.stdout + result.stderr)
    return result.stdout


def main():
    scratch = ROOT / "Files/validation-tests"
    scratch.mkdir(parents=True, exist_ok=True)
    run(["dotnet", "build", str(COMPARER), "-c", "Release", "--nologo", "-v", "quiet"])
    references = json.loads(run(["dotnet", "msbuild", str(COMPARER), "-target:ResolveReferences",
                                 "-getItem:ReferencePath", "-verbosity:quiet"]))
    runtime_reference = next(item["Identity"] for item in references["Items"]["ReferencePath"]
                             if Path(item["Identity"]).name == "System.Runtime.dll")
    with tempfile.TemporaryDirectory(dir=scratch) as name:
        work = Path(name)

        def compile_case(label, source, assembly_reference=None):
            folder = work / label
            folder.mkdir()
            project = PROJECT
            if assembly_reference is not None:
                project = project.replace("</Project>", '<ItemGroup><Reference Include="Synthetic.EnumReference"><HintPath>' +
                                          escape(str(assembly_reference)) + '</HintPath></Reference></ItemGroup></Project>')
            (folder / "Fixture.csproj").write_text(project)
            (folder / "Fixture.cs").write_text(source)
            run(["dotnet", "build", str(folder / "Fixture.csproj"), "-c", "Release", "--nologo", "-v", "quiet"])
            return folder / "bin/Release/net10.0/DeclarationFixtureTest.dll"

        # These projects have distinct output directories and no references to each other.
        # Build them together; the subsequent comparisons still run in a fixed order.
        with ThreadPoolExecutor(max_workers=4) as builds:
            baseline_build = builds.submit(compile_case, "baseline", SOURCE)
            mutation_build = builds.submit(compile_case, "mutation", SOURCE.replace("Pack = 4", "Pack = 8")
                                           .replace("UnmanagedType.U1", "UnmanagedType.I1")
                                           .replace("Limit = 31", "Limit = 37").replace("Marker(7)", "Marker(9)")
                                           .replace("where T : class", "where T : struct").replace("extra = 3", "extra = 4"))
            body_build = builds.submit(compile_case, "body-only", SOURCE.replace(
                "return value + extra", "return value - extra"))
            constructor_build = builds.submit(compile_case, "attribute-constructor", SOURCE.replace(
                'Choice((object)"fixture")', 'Choice("fixture")'))
            baseline = baseline_build.result()
            mutation = mutation_build.result()
            body_only = body_build.result()
            constructor_only = constructor_build.result()

        def compare(label, candidate, expected):
            output = work / label
            run(["dotnet", str(DLL), "--oracle", str(baseline), "--candidate", str(candidate),
                 "--reference-dir", str(Path(runtime_reference).parent), "--output", str(output)], expected)
            return json.loads((output / "report.json").read_text())

        assert compare("self", baseline, 0)["differenceCount"] == 0
        assert compare("body-comparison", body_only, 0)["differenceCount"] == 0
        constructor_report = compare("constructor-comparison", constructor_only, 1)
        assert constructor_report["differenceCount"] == 1
        assert constructor_report["differences"][0]["Key"].endswith("/attributes")
        assert constructor_report["diagnostics"] == []
        report = compare("mutated-comparison", mutation, 1)
        keys = [item["Key"] for item in report["differences"]]
        for expected_fragment in ("]Record", "/field:Enabled", "/field:Limit/constant",
                                  "/attributes", "/generic/0", "/parameter:2/constant"):
            assert any(expected_fragment in key for key in keys), expected_fragment
        assert report["diagnostics"] == []

        def compile_enum_reference(label, version, culture=""):
            folder = work / label
            folder.mkdir()
            (folder / "Reference.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                '<TargetFramework>net10.0</TargetFramework><AssemblyName>Synthetic.EnumReference</AssemblyName>'
                '<AssemblyVersion>' + version + '</AssemblyVersion></PropertyGroup></Project>')
            (folder / "Enum.cs").write_text('[assembly: System.Reflection.AssemblyCulture("' + culture + '")]\n'
                                          'namespace EnumLibrary { public enum Choice { Ready = 7 } }')
            run(["dotnet", "build", str(folder / "Reference.csproj"), "-c", "Release", "--nologo", "-v", "quiet"])
            return folder / "bin/Release/net10.0/Synthetic.EnumReference.dll"

        with ThreadPoolExecutor(max_workers=3) as builds:
            enum_v1_build = builds.submit(compile_enum_reference, "enum-v1", "1.0.0.0")
            enum_v2_build = builds.submit(compile_enum_reference, "enum-v2", "2.0.0.0")
            enum_culture_build = builds.submit(compile_enum_reference, "enum-culture", "1.0.0.0", "fr")
            enum_v1 = enum_v1_build.result()
            enum_v2 = enum_v2_build.result()
            enum_culture = enum_culture_build.result()
        external = compile_case("external-enum", SOURCE + '''
public sealed class EnumMarkerAttribute : Attribute
{
    public EnumMarkerAttribute(EnumLibrary.Choice value) { }
}
[EnumMarker(EnumLibrary.Choice.Ready), Choice((object)EnumLibrary.Choice.Ready)]
public sealed class ExternalEnumCase { }
''', enum_v1)

        def compare_external(label, enum_references, expected):
            output = work / label
            command = ["dotnet", str(DLL), "--oracle", str(external), "--candidate", str(external),
                       "--reference-dir", str(Path(runtime_reference).parent), "--output", str(output)]
            for reference in enum_references:
                command += ["--reference-dir", str(reference.parent)]
            run(command, expected)
            return json.loads((output / "report.json").read_text())

        for index, ordering in enumerate(([enum_v1, enum_v2, enum_culture], [enum_culture, enum_v2, enum_v1])):
            assert compare_external("enum-mixed-" + str(index), ordering, 0)["diagnostics"] == []
        for index, invalid in enumerate((enum_v2, enum_culture)):
            rejected = compare_external("enum-wrong-" + str(index), [invalid], 1)
            assert any("exactly one explicit identity match" in item for item in rejected["diagnostics"])
        duplicate = work / "enum-duplicate" / enum_v1.name
        duplicate.parent.mkdir()
        shutil.copyfile(enum_v1, duplicate)
        rejected = compare_external("enum-duplicate-comparison", [enum_v1, duplicate], 1)
        assert any("exactly one explicit identity match" in item for item in rejected["diagnostics"])
        print("Declaration comparison checks passed: self, body exclusion, overloaded attribute constructor, six declaration mutations.")
        print("Enum references: exact version/culture selection in both directory orders; wrong identities and duplicate exact matches rejected.")


if __name__ == "__main__":
    main()
