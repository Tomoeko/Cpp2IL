"""Mutation checks for the read-only declaration comparison boundary."""

import json
from pathlib import Path
import subprocess
import tempfile


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
[StructLayout(LayoutKind.Explicit, Pack = 4, Size = 16)]
public struct Record
{
    [FieldOffset(0), MarshalAs(UnmanagedType.U1)] public bool Enabled;
}
public sealed class Container<T> where T : class { public T Item; }
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

        def compile_case(label, source):
            folder = work / label
            folder.mkdir()
            (folder / "Fixture.csproj").write_text(PROJECT)
            (folder / "Fixture.cs").write_text(source)
            run(["dotnet", "build", str(folder / "Fixture.csproj"), "-c", "Release", "--nologo", "-v", "quiet"])
            return folder / "bin/Release/net10.0/DeclarationFixtureTest.dll"

        baseline = compile_case("baseline", SOURCE)
        mutation = compile_case("mutation", SOURCE.replace("Pack = 4", "Pack = 8")
                                .replace("UnmanagedType.U1", "UnmanagedType.I1")
                                .replace("Limit = 31", "Limit = 37").replace("Marker(7)", "Marker(9)")
                                .replace("where T : class", "where T : struct").replace("extra = 3", "extra = 4"))
        body_only = compile_case("body-only", SOURCE.replace("return value + extra", "return value - extra"))

        def compare(label, candidate, expected):
            output = work / label
            run(["dotnet", str(DLL), "--oracle", str(baseline), "--candidate", str(candidate),
                 "--reference-dir", str(Path(runtime_reference).parent), "--output", str(output)], expected)
            return json.loads((output / "report.json").read_text())

        assert compare("self", baseline, 0)["differenceCount"] == 0
        assert compare("body-comparison", body_only, 0)["differenceCount"] == 0
        report = compare("mutated-comparison", mutation, 1)
        keys = [item["Key"] for item in report["differences"]]
        for expected_fragment in ("]Record", "/field:Enabled", "/field:Limit/constant",
                                  "/attributes", "/generic/0", "/parameter:2/constant"):
            assert any(expected_fragment in key for key in keys), expected_fragment
        assert report["diagnostics"] == []
        print("Declaration comparison checks passed: self, body exclusion, six declaration mutations.")


if __name__ == "__main__":
    main()
