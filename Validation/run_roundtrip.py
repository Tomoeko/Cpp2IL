#!/usr/bin/env python3
"""Run the synthetic player-only recovery, IL verification and exact Unity round trip."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

from run_fixture import ROOT, VERSION, PROFILES as FIXTURE_PROFILES, run_process, write_json


PROFILES = {name: FIXTURE_PROFILES[name] for name in (
    "loop-calls", "word-fields", "integer-extensions", "byte-fields",
    "float-comparisons", "components", "metadata-literal", "division", "arithmetic", "integers", "scalar-structs", "shifts",
)}
PLAYER_FILES = ("GameAssembly.dll", "RecoveryFixture_Data/il2cpp_data/Metadata/global-metadata.dat")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def managed_oracle(run_directory, assembly):
    path = run_directory / "player/RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed" / (assembly + ".dll")
    if not path.is_file():
        raise ValueError("The original or rebuilt native run did not retain its managed declaration oracle")
    return path


def checked_baseline(directory, profile):
    receipt = json.loads((directory / "receipt.json").read_text(encoding="utf-8"))
    if receipt.get("status") != "passed" or receipt.get("sourceKind") != "synthetic-baseline":
        raise ValueError("Baseline must be a successful original synthetic fixture run")
    if receipt.get("profile", "arithmetic") != profile:
        raise ValueError("Baseline fixture profile does not match the requested round trip")
    build = receipt["stages"]["nativeBuild"]
    expected = {"unityVersion": VERSION, "target": "StandaloneWindows64", "backend": "IL2CPP",
                "compilerConfiguration": "Release", "development": False, "errors": 0,
                "result": "Succeeded"}
    if any(build.get(key) != value for key, value in expected.items()):
        raise ValueError("Baseline native build does not match the required profile")
    if receipt["stages"]["playerBehavior"].get("status") != "passed":
        raise ValueError("Baseline must have passed its native behavior checks")
    files = {item["path"]: item for item in receipt["playerInputs"]}
    for relative in PLAYER_FILES:
        if digest(directory / "player-input" / relative) != files[relative]["sha256"]:
            raise ValueError("Baseline player input changed since its build receipt")
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--wine")
    parser.add_argument("--toolchain-root", type=Path)
    parser.add_argument("--cpp2il", type=Path, required=True, help="Built Cpp2IL.dll, executed with dotnet")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--reference-dir", action="append", type=Path, required=True)
    parser.add_argument("--il-reference-dir", action="append", type=Path,
                        help="Explicit ILVerify reference set; defaults to --reference-dir. Source/declaration resolution keeps the full source set.")
    parser.add_argument("--baseline-run", type=Path, help="Reuse a verified original synthetic baseline; otherwise build it")
    parser.add_argument("--install-ilverify", action="store_true")
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600, help="Per child-stage deadline in seconds")
    parser.add_argument("--profile", choices=sorted(PROFILES), default="arithmetic")
    args = parser.parse_args()
    profile = PROFILES[args.profile]
    assembly, expected_methods = profile["assembly"], profile["methods"]
    directory = args.run_dir.expanduser().resolve()
    private = (ROOT / "Files").resolve()
    if directory == private or private not in directory.parents or directory.exists():
        parser.error("--run-dir must be a fresh child directory under Files/")
    if subprocess.run(["git", "check-ignore", "--quiet", str(directory)], cwd=ROOT).returncode != 0:
        parser.error("--run-dir must be ignored by Git")
    if args.timeout <= 0 or not args.cpp2il.is_file():
        parser.error("Provide a positive timeout and an existing built Cpp2IL.dll")
    directory.mkdir(parents=True)
    receipt = {"schemaVersion": 1, "status": "running", "scope": assembly, "profile": args.profile,
               "unityVersion": VERSION, "recoveryInputs": "isolated player binary and metadata only",
               "declarationFidelity": "unverified", "scriptAssetBindings": "out_of_scope",
               "stages": {}, "commands": []}
    # Snapshot the built tool and its adjacent managed dependencies so a concurrent repository
    # build cannot change the recovery implementation halfway through a validation run.
    tool_directory = directory / "tool"
    tool_directory.mkdir()
    receipt["toolFiles"] = []
    for path in sorted(args.cpp2il.resolve().parent.iterdir()):
        if path.is_file() and path.suffix in {".dll", ".json"}:
            target = tool_directory / path.name
            shutil.copyfile(path, target)
            receipt["toolFiles"].append({"path": path.name, "sha256": digest(target)})
    tool = tool_directory / args.cpp2il.name
    receipt["commit"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    receipt["workingTreeDirty"] = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT))

    def run(name, command, timeout=None):
        # Cpp2IL discovers optional plugins relative to its current directory. Keep
        # recovery in the owned tool snapshot so ambient plugins cannot alter it.
        result = run_process(command, os.environ.copy(), directory / (name + ".log"), timeout or args.timeout,
                             cwd=tool_directory if name == "recovery" else ROOT)
        receipt["commands"].append({"stage": name, **result})
        if result["timedOut"] or result["exitCode"] != 0:
            raise ValueError(name + " failed or timed out; inspect its local log")

    fixture_command = [sys.executable, str(ROOT / "Validation/run_fixture.py"),
                       "--editor", str(args.editor.expanduser().resolve()), "--stage", "run", "--timeout", str(args.timeout),
                       "--profile", args.profile]
    if args.wine:
        fixture_command += ["--wine", args.wine]
    if args.toolchain_root:
        fixture_command += ["--toolchain-root", str(args.toolchain_root.expanduser().resolve())]
    try:
        baseline = args.baseline_run.expanduser().resolve() if args.baseline_run else directory / "baseline"
        if not args.baseline_run:
            run("baseline", fixture_command + ["--run-dir", str(baseline)], args.timeout * 2 + 30)
        original = checked_baseline(baseline, args.profile)
        receipt["baselineReceipt"] = {"path": str(baseline / "receipt.json"), "sha256": digest(baseline / "receipt.json")}
        receipt["stages"]["original"] = original["stages"]

        # Only these two shipped inputs reach the recovery command. No source, generated C++,
        # application managed oracle, debug symbols or analysis database is supplied.
        player = directory / "recovery-input"
        receipt["inputFiles"] = []
        for relative in PLAYER_FILES:
            source = baseline / "player-input" / relative
            target = player / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
            receipt["inputFiles"].append({"path": relative, "sha256": digest(target)})
        references = [str(path.expanduser().resolve()) for path in args.reference_dir]
        il_references = [str(path.expanduser().resolve()) for path in (args.il_reference_dir or args.reference_dir)]
        receipt["referenceConfiguration"] = {"sourceAndDeclarations": references, "managedIl": il_references,
                                             "managedIlExplicit": args.il_reference_dir is not None}
        if any((Path(path) / (assembly + ".dll")).exists() for path in references + il_references):
            raise ValueError("Reference directories must not contain the original application assembly")
        recovered = directory / "recovered"
        run("recovery", [args.dotnet, str(tool), "--force-binary-path", str(player / PLAYER_FILES[0]),
                         "--force-metadata-path", str(player / PLAYER_FILES[1]), "--force-unity-version", VERSION,
                         "--output-as", "cs_unity", "--unity-source-assemblies", assembly,
                         "--unity-reference-dir", *references, "--strict-recovery", "--output-to", str(recovered)])
        report = json.loads((recovered / "source-recovery-report.json").read_text(encoding="utf-8"))
        methods = [method for method in report["Methods"] if method["AssemblyName"] == assembly]
        if not report["AnalysisCompleted"] or len(methods) != expected_methods or any(method["Disposition"] != "Emitted" for method in methods):
            raise ValueError("The selected fixture methods were not all recovered without detected degradation")
        project = recovered / "UnityProject"
        emission = json.loads((project / "source-emission-report.json").read_text(encoding="utf-8"))
        if emission["SourceGeneration"] != "generated" or emission["Diagnostics"]:
            raise ValueError("Source emission did not complete without diagnostics")
        receipt["stages"]["recovery"] = {"status": "passed", "inputMethods": report["InputMethodCount"],
                                           "selectedMethods": len(methods), "selectedUnresolved": 0}

        verify = [sys.executable, str(ROOT / "Validation/verify_managed_il.py"), "--assembly",
                  str(project / "RecoveredManaged" / (assembly + ".dll")), "--output-dir", str(directory / "il-verification")]
        for reference in il_references:
            verify += ["--reference-dir", reference]
        if args.install_ilverify:
            verify += ["--install-tool"]
        run("il-verification", verify)
        verification = json.loads((directory / "il-verification/result.json").read_text(encoding="utf-8"))
        if verification["status"] != "passed":
            raise ValueError("Typed IL verification did not pass")
        receipt["stages"]["managedIl"] = {"status": "passed", "toolVersion": verification["actual_tool_version"]}

        # Consult managed validation oracles only after player-only recovery has completed.
        # Snapshot the independent comparer too, since other local work may rebuild it.
        comparison_project = ROOT / "Validation/DeclarationComparer/DeclarationComparer.csproj"
        run("build-declaration-comparer", [args.dotnet, "build", str(comparison_project), "-c", "Release", "--nologo", "-v", "quiet"])
        comparison_directory = directory / "declaration-comparer"
        comparison_directory.mkdir()
        receipt["comparisonToolFiles"] = []
        for path in sorted((comparison_project.parent / "bin/Release/net10.0").iterdir()):
            if path.is_file() and path.suffix in {".dll", ".json"}:
                target = comparison_directory / path.name
                shutil.copyfile(path, target)
                receipt["comparisonToolFiles"].append({"path": path.name, "sha256": digest(target)})
        original_managed = managed_oracle(baseline, assembly)
        unstripped = baseline / "project/Library/ScriptAssemblies" / (assembly + ".dll")

        def compare_declarations(name, candidate):
            output = directory / name
            command = [args.dotnet, str(comparison_directory / "DeclarationComparer.dll"),
                       "--oracle", str(original_managed), "--candidate", str(candidate),
                       "--unstripped", str(unstripped), "--output", str(output)]
            for reference in references:
                command += ["--reference-dir", reference]
            run(name, command)
            comparison = json.loads((output / "report.json").read_text(encoding="utf-8"))
            if comparison["status"] != "passed" or comparison["differenceCount"] != 0 or comparison["diagnostics"]:
                raise ValueError("Declaration comparison failed; inspect its local report")
            receipt["stages"][name] = {
                "status": "passed", "scope": "comparer projection against original stripped managed declarations",
                "differenceCount": 0, "counts": comparison["candidate"]["counts"],
                "originalStrippingLosses": len(comparison["stripping"]["lostIdentities"]),
                "report": str(output / "report.json"), "reportSha256": digest(output / "report.json"),
            }

        compare_declarations("recoveredDeclarations", project / "RecoveredManaged" / (assembly + ".dll"))

        replacement = directory / "replacement"
        run("replacement", fixture_command + ["--source-dir", str(project / "Assets/Recovered" / assembly),
                                              "--run-dir", str(replacement)], args.timeout * 2 + 30)
        rebuilt = json.loads((replacement / "receipt.json").read_text(encoding="utf-8"))
        if rebuilt["status"] != "passed" or rebuilt["sourceKind"] != "replacement-source":
            raise ValueError("Recovered-source fixture did not pass its independent gates")
        settings = ("unityVersion", "target", "backend", "compilerConfiguration", "apiCompatibility", "stripping",
                    "codeGeneration", "stripEngineCode", "scriptingDefineSymbols", "development")
        if any(original["stages"]["nativeBuild"][key] != rebuilt["stages"]["nativeBuild"][key] for key in settings):
            raise ValueError("Original and recovered native build settings differ")
        receipt["stages"]["recovered"] = rebuilt["stages"]
        compare_declarations("rebuiltDeclarations", managed_oracle(replacement, assembly))
        receipt["declarationFidelity"] = "passed_against_stripped_oracle"
        receipt["artifacts"] = [{"path": str(path.relative_to(directory)), "sha256": digest(path)}
                                for path in sorted(recovered.rglob("*")) if path.is_file()]
        for item in receipt["inputFiles"]:
            if digest(player / item["path"]) != item["sha256"]:
                raise ValueError("Recovery input changed during validation")
        for item in receipt["toolFiles"]:
            if digest(tool_directory / item["path"]) != item["sha256"]:
                raise ValueError("Recovery tool snapshot changed during validation")
        for item in receipt["comparisonToolFiles"]:
            if digest(comparison_directory / item["path"]) != item["sha256"]:
                raise ValueError("Declaration comparer snapshot changed during validation")
        receipt["status"] = "passed"
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        receipt["status"] = "failed"
        receipt["error"] = str(error)
    finally:
        write_json(directory / "roundtrip.json", receipt)
    print(receipt["status"] + ": " + str(directory / "roundtrip.json"))
    return 0 if receipt["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
