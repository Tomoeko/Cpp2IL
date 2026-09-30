#!/usr/bin/env python3
"""Validate independent recovered assemblies in one fresh exact-target Unity player."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time

from run_fixture import (ROOT, VERSION, EMBEDDED_FIXTURE_PACKAGES, run_process, write_json,
                         verify_behavior, verify_embedded_fixture_dependency)
from run_roundtrip import (PROFILES, checked_baseline, current_source_files, digest,
                          checked_managed_oracle_snapshots, managed_oracle, verify_snapshot_inputs,
                          owned_snapshot_file, PLAYER_FILES)


BUILD_SETTINGS = ("unityVersion", "host", "target", "backend", "compilerConfiguration",
                  "apiCompatibility", "stripping", "codeGeneration", "stripEngineCode",
                  "scriptingDefineSymbols", "development")


def verify_batch_player(batch_directory, batch):
    files = batch["playerInputs"]
    if not isinstance(files, list) or not files:
        raise ValueError("Batch player input inventory is missing")
    recorded = {item["path"]: item["sha256"] for item in files}
    if len(recorded) != len(files) or not set((*PLAYER_FILES, "RecoveryFixture.exe", "UnityPlayer.dll")) <= recorded.keys():
        raise ValueError("Batch player input inventory is incomplete or duplicated")
    root = batch_directory / "player-input"
    for relative, expected in recorded.items():
        path = root / relative
        if (Path(relative).is_absolute() or ".." in Path(relative).parts or
                not path.resolve().is_relative_to(root.resolve()) or
                not owned_snapshot_file(batch_directory, path) or digest(path) != expected):
            raise ValueError("Rebuilt player input changed during validation")


def checked_batch_profile(directory, prepared, batch_directory, batch):
    """Require fresh gates, byte-identical source and the original assembly boundary."""
    if prepared.get("status") != "awaiting-unity-batch" or batch.get("status") != "passed":
        raise ValueError("Prepared recovery and completed Unity batch are required")
    profile = prepared["profile"]
    record = batch["profiles"][profile]
    assembly = PROFILES[profile]["assembly"]
    if prepared["scope"] != assembly or record["assembly"] != assembly:
        raise ValueError("Batch changed the fixture assembly boundary")
    source = directory / "recovered/UnityProject/Assets/Recovered" / assembly
    if Path(prepared["batchSourceDirectory"]).resolve() != source.resolve():
        raise ValueError("Prepared source directory differs from the recovered assembly")
    expected = current_source_files(source)
    recorded = record["sourceFiles"]
    if not isinstance(recorded, list) or len(recorded) != len(expected):
        raise ValueError("Batch source inventory differs from recovered source")
    actual = {item["path"]: item["sha256"] for item in recorded}
    if len(actual) != len(recorded) or actual != expected:
        raise ValueError("Batch did not compile the byte-identical recovered source")
    if current_source_files(batch_directory / "project/Assets" / assembly) != expected:
        raise ValueError("Compiled batch source changed after its receipt")
    stages = record["stages"]
    if set(stages) != {"unityCompilation", "editorBehavior", "nativeBuild", "playerBehavior"}:
        raise ValueError("Batch profile is missing a validation gate")
    if any(stage.get("status") != "passed" for stage in stages.values()):
        raise ValueError("Every exact-target Unity batch gate must pass")
    original = prepared["stages"]["original"]
    build = stages["nativeBuild"]
    if (build.get("unityVersion") != VERSION or build.get("result") != "Succeeded" or
            build.get("errors") != 0 or any(build.get(key) != original["nativeBuild"].get(key)
                                          for key in BUILD_SETTINGS)):
        raise ValueError("Original and batched rebuilt native settings differ")
    if stages["unityCompilation"].get("version") != VERSION:
        raise ValueError("Batch source compilation used an unexpected Unity version")
    for name, platform in (("editorBehavior", "WindowsEditor"), ("playerBehavior", "WindowsPlayer")):
        stage = stages[name]
        if (stage.get("profile") != profile or stage.get("platform") != platform or
                stage.get("observations") != original[name].get("observations") or
                stage.get("methods") != original[name].get("methods")):
            raise ValueError("Batch behavior scope differs from the original fixture")
        report = (batch_directory / "project/Reports/batch" / profile / "editor-behavior.json"
                  if name == "editorBehavior" else batch_directory / "player-behavior" / (profile + ".json"))
        if not owned_snapshot_file(batch_directory, report):
            raise ValueError("Retained batch behavior report is missing or linked")
        evidence = {item["path"]: item["sha256"] for item in record["behaviorReports"]}
        if evidence.get(report.relative_to(batch_directory).as_posix()) != digest(report):
            raise ValueError("Retained batch behavior report changed since its receipt")
        if verify_behavior(report, "editor" if name == "editorBehavior" else "player", profile) != stage:
            raise ValueError("Retained batch behavior differs from the reported gate")
    if profile in EMBEDDED_FIXTURE_PACKAGES:
        dependency = record["auxiliaryDependencies"]
        original_dependency = prepared["batchOriginalDependencies"]
        if (dependency.get("provenance") != "synthetic-explicit-package" or
                dependency.get("embeddedPackageFiles") != original_dependency["embeddedPackageFiles"]):
            raise ValueError("Batch synthetic package sources differ from the original fixture")
        verify_embedded_fixture_dependency(batch_directory / "project", dependency, profile)
    verify_snapshot_inputs(directory, prepared)
    return stages


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-run", action="append", required=True, metavar="PROFILE=PATH")
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--wine")
    parser.add_argument("--toolchain-root", type=Path)
    parser.add_argument("--cpp2il", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--reference-dir", action="append", type=Path, required=True)
    parser.add_argument("--il-reference-dir", action="append", type=Path)
    parser.add_argument("--install-ilverify", action="store_true")
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=180, help="Whole-batch execution budget in seconds")
    parser.add_argument("--code-generation", choices=("OptimizeSpeed", "OptimizeSize"), default="OptimizeSpeed")
    args = parser.parse_args(argv)
    baselines = {}
    for value in args.baseline_run:
        name, separator, path = value.partition("=")
        if not separator or name not in PROFILES or name in baselines or not path:
            parser.error("Baselines must be unique supported PROFILE=PATH pairs")
        if name == "external-references":
            parser.error("The plug-in fixture requires the independent round-trip runner")
        baselines[name] = Path(path).expanduser().resolve()
    directory = args.run_dir.expanduser().resolve()
    if (args.timeout <= 0 or directory.exists() or
            not directory.is_relative_to((ROOT / "Files").resolve()) or
            directory == (ROOT / "Files").resolve() or
            subprocess.run(["git", "check-ignore", "--quiet", str(directory)], cwd=ROOT).returncode):
        parser.error("Provide a positive budget and a fresh ignored child directory under Files/")
    directory.mkdir(parents=True)
    started = time.monotonic()
    receipt = {"schemaVersion": 1, "status": "running", "unityVersion": VERSION,
               "scope": list(baselines), "executionBudgetSeconds": args.timeout,
               "validationArrangement": "independent original players; one rebuilt player with separate recovered assemblies",
               "commands": [], "profiles": {}}

    def remaining():
        seconds = args.timeout - (time.monotonic() - started)
        if seconds <= 0:
            raise ValueError("Whole-batch execution budget exhausted")
        return max(1, int(seconds))

    def run(stage, command, cleanup_grace=0):
        outcome = run_process(command, os.environ.copy(), directory / (stage + ".log"),
                              remaining() + cleanup_grace, cwd=ROOT)
        receipt["commands"].append({"stage": stage, **outcome})
        write_json(directory / "roundtrip.json", receipt)
        if outcome["timedOut"] or outcome["exitCode"] != 0:
            raise ValueError(stage + " failed or timed out; inspect its local log")

    try:
        for name, baseline in baselines.items():
            checked_baseline(baseline, name, expected_code_generation=args.code_generation)
            target = directory / name
            command = [sys.executable, str(ROOT / "Validation/run_roundtrip.py"),
                       "--profile", name, "--editor", str(args.editor), "--cpp2il", str(args.cpp2il),
                       "--dotnet", args.dotnet, "--baseline-run", str(baseline), "--run-dir", str(target),
                       "--timeout", str(remaining()), "--code-generation", args.code_generation, "--defer-unity"]
            if args.wine:
                command += ["--wine", args.wine]
            if args.toolchain_root:
                command += ["--toolchain-root", str(args.toolchain_root)]
            for path in args.reference_dir:
                command += ["--reference-dir", str(path)]
            for path in args.il_reference_dir or ():
                command += ["--il-reference-dir", str(path)]
            if args.install_ilverify:
                command.append("--install-ilverify")
            if name in EMBEDDED_FIXTURE_PACKAGES:
                configuration = EMBEDDED_FIXTURE_PACKAGES[name]
                reference_map = directory / (name + "-reference-map.json")
                write_json(reference_map, {"references": [{"assembly": Path(configuration["assembly"]).stem,
                                                           "kind": "asmdef"}]})
                command += ["--external-reference-map", str(reference_map)]
            run("prepare-" + name, command)
            prepared = json.loads((target / "roundtrip.json").read_text(encoding="utf-8"))
            if prepared.get("status") != "awaiting-unity-batch":
                raise ValueError("Profile did not finish recovery preparation")
            if name in EMBEDDED_FIXTURE_PACKAGES:
                original = json.loads((baseline / "receipt.json").read_text(encoding="utf-8"))
                prepared["batchOriginalDependencies"] = original["auxiliaryDependencies"]
                write_json(target / "roundtrip.json", prepared)
            receipt["profiles"][name] = {"status": "awaiting-unity-batch", "receipt": name + "/roundtrip.json"}

        configuration_path = directory / "batch-fixtures.json"
        write_json(configuration_path, {"profiles": [
            {"profile": name, "sourceDirectory": str(directory / name / "recovered/UnityProject/Assets/Recovered" /
                                                       PROFILES[name]["assembly"])} for name in baselines]})
        batch_directory = directory / "rebuilt-batch"
        command = [sys.executable, str(ROOT / "Validation/run_fixture_batch.py"),
                   "--fixtures", str(configuration_path), "--editor", str(args.editor),
                   "--run-dir", str(batch_directory), "--timeout", str(remaining()),
                   "--code-generation", args.code_generation]
        if args.wine:
            command += ["--wine", args.wine]
        if args.toolchain_root:
            command += ["--toolchain-root", str(args.toolchain_root)]
        # The child owns the editor deadline and process-group cleanup. Allow its
        # termination grace to finish so a timed-out build cannot orphan Unity.
        run("rebuilt-batch", command, cleanup_grace=15)
        batch_path = batch_directory / "receipt.json"
        batch = json.loads(batch_path.read_text(encoding="utf-8"))
        if set(batch["profiles"]) != set(baselines):
            raise ValueError("Rebuilt batch profile set differs")
        verify_batch_player(batch_directory, batch)
        batch_hash = digest(batch_path)
        rebuilt_oracles = {}
        finalized = {}
        for name in baselines:
            target = directory / name
            path = target / "roundtrip.json"
            prepared = json.loads(path.read_text(encoding="utf-8"))
            stages = checked_batch_profile(target, prepared, batch_directory, batch)
            baseline_path = baselines[name] / "receipt.json"
            if digest(baseline_path) != prepared["baselineReceipt"]["sha256"]:
                raise ValueError("Original baseline receipt changed during batch validation")
            assembly = PROFILES[name]["assembly"]
            oracles = checked_managed_oracle_snapshots(target, assembly, prepared["managedOracles"]["files"])
            comparison = target / "rebuiltDeclarations"
            candidate = managed_oracle(batch_directory, assembly)
            candidate_hash = digest(candidate)
            rebuilt_oracles[assembly] = candidate_hash
            command = [args.dotnet, str(target / "declaration-comparer/DeclarationComparer.dll"),
                       "--oracle", str(oracles["stripped"]), "--unstripped", str(oracles["unstripped"]),
                       "--candidate", str(candidate), "--output", str(comparison)]
            for reference in prepared["referenceConfiguration"]["declarations"]:
                command += ["--reference-dir", reference]
            run("compare-" + name, command)
            report_path = comparison / "report.json"
            report = json.loads(report_path.read_text(encoding="utf-8"))
            if report.get("status") != "passed" or report.get("differenceCount") != 0 or report.get("diagnostics"):
                raise ValueError("Batched rebuilt declarations differ from the original fixture")
            prepared["stages"]["recovered"] = stages
            prepared["stages"]["rebuiltDeclarations"] = {
                "status": "passed", "scope": "comparer projection against original stripped managed declarations",
                "differenceCount": 0, "counts": report["candidate"]["counts"],
                "originalStrippingLosses": len(report["stripping"]["lostIdentities"]),
                "report": str(report_path), "reportSha256": digest(report_path),
                "candidateSha256": candidate_hash}
            verify_snapshot_inputs(target, prepared)
            prepared["batchValidation"] = {"receipt": str(batch_path), "receiptSha256": batch_hash,
                                           "scope": list(baselines), "separateFixtureAssemblies": True,
                                           "packageGraph": "combined explicit synthetic fixture dependencies"}
            prepared["declarationFidelity"] = "passed_against_stripped_oracle"
            prepared["status"] = "passed"
            finalized[name] = prepared
        if digest(batch_path) != batch_hash:
            raise ValueError("Unity batch receipt changed during declaration comparison")
        verify_batch_player(batch_directory, batch)
        for assembly, expected in rebuilt_oracles.items():
            if digest(managed_oracle(batch_directory, assembly)) != expected:
                raise ValueError("Rebuilt managed declaration oracle changed during comparison")
        remaining()
        # Publish individual successes only after all shared evidence and the
        # execution budget have been checked. A late failure leaves them pending.
        for name, prepared in finalized.items():
            write_json(directory / name / "roundtrip.json", prepared)
            receipt["profiles"][name]["status"] = "passed"
        receipt["batchReceipt"] = {"path": str(batch_path), "sha256": batch_hash}
        receipt["status"] = "passed"
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        receipt["status"] = "failed"
        receipt["error"] = str(error)
    finally:
        receipt["seconds"] = round(time.monotonic() - started, 3)
        write_json(directory / "roundtrip.json", receipt)
    print(receipt["status"] + ": " + str(directory / "roundtrip.json"), flush=True)
    return 0 if receipt["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
