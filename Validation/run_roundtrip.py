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

import declaration_comparer_snapshot as comparer_snapshot
from behavior_oracle import read_report, _same_type_and_value

from run_fixture import (ROOT, VERSION, PROFILES as FIXTURE_PROFILES, run_process,
                         write_json, resolved_package_lock_sha256, embedded_reference_lock_sha256,
                         embedded_package_lock_sha256, profile_harness_directory,
                         EMBEDDED_FIXTURE_PACKAGES, verify_external_reference_fixture,
                         verify_embedded_fixture_dependency, verify_behavior, owned_snapshot_file,
                         checked_behavior_report_files)


PROFILES = {name: FIXTURE_PROFILES[name] for name in (
    "boolean-composition",
    "conditional-boolean-store",
    "conditional-generic-boolean-store",
    "conditional-generic-terminal-store",
    "instance-parameter-reference-read",
    "field-parameter-boolean-array-store",
    "folded-boolean-array-store",
    "boolean-array-fill-loop",
    "base-effect-boolean-tail",
    "call-result-boolean-tail",
    "conditional-call-result-tail",
    "lookup-guard-managed-throw",
    "guarded-array-operations",
    "array-call-origins",
    "guarded-array-tail-invocation",
    "scalar-float-selection",
    "scalar-float-conversion",
    "scalar-int32-single-conversion",
    "scalar-double-accumulator",
    "native-boolean-toggle-invocation",
    "native-boolean-predicate-invocation",
    "scalar-float-conversion-composition",
    "native-null-checked-invocation",
    "native-scalar-pair-invocation",
    "native-scalar-field-invocation",
    "native-scalar-invocation-effects",
    "native-subnormal-field-store",
    "native-derived-receiver-invocation",
    "scalar-positive-zero-leaf",
    "fixed-boolean-conjunction",
    "boolean-literal-store",
    "parameter-boolean-array-store",
    "scalar-field-comparison",
    "integer-literal-store",
    "aggregate-scalar-compare",
    "boolean-parameter-class-test",
    "sealed-parameter-class-test",
    "guarded-array-length",
    "narrow-scalar-getter",
    "final-interface-boolean-getter",
    "ancestor-cctor-interface-getter",
    "integer-truncation",
    "small-aggregate-getter",
    "wide-field-low32",
    "byref-integer-halves",
    "guarded-scalar-accessor",
    "ordered-generic-tail-field",
    "enum-integer-conversion",
    "closed-generic-storage",
    "dynamic-virtual-dispatch",
    "open-generic-early-field",
    "open-generic-prefix-operations",
    "array-read-increment",
    "object-reference-store",
    "empty-object-constructor",
    "string-reference-store",
    "nested-literal-store",
    "call-before-capture-boolean-store",
    "nullable-delegate-field-tail",
    "nested-byte-field-read",
    "array-element-argument-tail",
    "final-override-boolean-getter",
    "false-boolean-virtual-tail",
    "call-result-string-tail",
    "cctor-boolean-getter",
    "side-effect-class-cctor",
    "field-plus-one-reference-array",
    "internal-call-field",
    "enum-field-array",
    "parameter-class-test",
    "catch-divide", "exception-regions", "explicit-class-cast", "array-access", "array-sequence", "field-array", "field-boolean-array", "field-boolean-array-read", "range-array-read", "constructed-base-boolean-array", "narrow-array", "float-array", "word-array", "reference-array", "boolean-getter", "boolean-getter-metadata", "virtual-string-call", "generic-dispatch", "guarded-sink", "folded-state-constructor", "folded-literal-constructor", "shared-inert-constructor", "float-initializer-constructor", "scalar-wrapper", "scalar-wrapper-cctor", "constructor-thunk-chain", "boolean-parameter-branch", "narrow-test-arithmetic", "byte-mask-parameter", "byte-mask-one", "array-call",
    "enum-passthrough", "static-field-getter", "static-word-getter", "reference-field", "native-int-field", "reference-null", "reference-field-null", "wide-reference-null", "sequential-null-guards", "call-result-null-guards",
    "reference-store", "external-references", "numerics-reference", "byte-threshold", "dense-switch", "composed-array", "composed-read", "parameter-array", "field-guard", "inherited-field-guard",
    "zero-arg-field-call", "reference-tail-call", "enum-return-tail", "boolean-tail-field-call", "call-result-boolean-store", "call-result-tail-guard", "call-result-false-tail", "call-result-engine-false-tail", "engine-component-false-tail", "dual-result-tail-guard", "triple-literal-guard", "ordered-call-tail-guard", "ordered-noarg-tail-guard", "unsealed-zero-store", "forwarded-argument", "struct-forward-call", "struct-static-forward-call", "scalar-truncation", "scalar-zero-return", "arithmetic-zero-flag", "loop-calls", "word-fields",
    "nested-boolean-store",
    "nested-boolean-getter",
    "float-forward-store",
    "nested-single-getter",
    "fixed-reference-array",
    "folded-reference-array",
    "owner-indexed-enum-array",
    "nested-array-call",
    "array-element-scalar-field",
    "inherited-reference-array-read",
    "fixed-scalar-array",
    "array-element-store",
    "shared-abi",
    "unused-reference-nested-store",
    "iterator-factory",
    "iterator-factory-manual",
    "iterator-factory-variant",
    "iterator-factory-direct-ctor",
    "static-scalar-setter",
    "instance-reference-property",
    "instance-reference-setter",
    "literal-concat",
    "class-cast-lookup",
    "guarded-boxed-cast",
    "runtime-cast-concat",
    "static-literal-concat",
    "throw-only",
    "conditional-managed-throw",
    "virtual-tail-dispatch",
    "layered-virtual-tail-dispatch",
    "metadata-guard-move",
    "metadata-guard-parameter",
    "integer-extensions", "register-zero-extension", "byte-fields", "float-comparisons", "xmm-spill", "xmm-ref-mutation",
    "components", "metadata-literal", "division", "arithmetic", "integers",
    "scalar-structs", "shifts",
)}
PLAYER_FILES = ("GameAssembly.dll", "RecoveryFixture_Data/il2cpp_data/Metadata/global-metadata.dat")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_player_inputs(directory, recorded):
    """Authenticate the complete isolated runtime, including behavior dependencies."""
    if (not isinstance(recorded, list) or not recorded or
            any(not isinstance(item, dict) or not isinstance(item.get("path"), str) or
                not isinstance(item.get("sha256"), str) for item in recorded)):
        raise ValueError("Native player input inventory is missing or malformed")
    files = {item["path"]: item["sha256"] for item in recorded}
    if len(files) != len(recorded) or not set((*PLAYER_FILES, "RecoveryFixture.exe", "UnityPlayer.dll")) <= files.keys():
        raise ValueError("Native player input inventory is incomplete or duplicated")
    root = directory / "player-input"
    actual = set()
    for path in root.rglob("*"):
        if path.is_symlink():
            raise ValueError("Native player input may not contain symbolic links")
        if path.is_file():
            actual.add(path.relative_to(root).as_posix())
    if actual != files.keys():
        raise ValueError("Native player input path set differs from its build receipt")
    for relative, expected in files.items():
        path = root / relative
        if (Path(relative).is_absolute() or ".." in Path(relative).parts or
                not path.resolve().is_relative_to(root.resolve()) or
                not owned_snapshot_file(directory, path) or digest(path) != expected):
            raise ValueError("Native player input changed since its build receipt")


def verify_snapshot_inputs(directory, receipt):
    """Authenticate the isolated player, tools, oracles and recovered artifacts."""
    for key, root in (("inputFiles", "recovery-input"), ("toolFiles", "tool"),
                      ("comparisonToolFiles", "declaration-comparer"), ("artifacts", "")):
        for item in receipt[key]:
            relative = Path(item["path"])
            path = directory / root / relative
            if (relative.is_absolute() or ".." in relative.parts or
                    not path.resolve().is_relative_to((directory / root).resolve()) or
                    not owned_snapshot_file(directory, path) or digest(path) != item["sha256"]):
                raise ValueError("Validation snapshot changed: " + key)
    if "comparisonToolManifest" in receipt:
        record = receipt["comparisonToolManifest"]
        manifest = directory / comparer_snapshot.MANIFEST
        if record.get("path") != manifest.name or not owned_snapshot_file(directory, manifest):
            raise ValueError("Declaration comparer manifest is missing or linked")
        snapshot = comparer_snapshot.checked(ROOT, manifest, record.get("sha256"))
        if snapshot["files"] != receipt["comparisonToolFiles"]:
            raise ValueError("Declaration comparer receipt differs from its authenticated snapshot")
    checked_managed_oracle_snapshots(directory, receipt["scope"], receipt["managedOracles"]["files"])
    checked_behavior_oracles(directory, receipt["profile"], receipt.get("behaviorOracles"))


def managed_oracle(run_directory, assembly):
    path = run_directory / "player/RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed" / (assembly + ".dll")
    if not owned_snapshot_file(run_directory, path):
        raise ValueError("The original or rebuilt native run did not retain its managed declaration oracle")
    return path


def snapshot_managed_oracles(baseline, directory, assembly, reference_directories=()):
    """Freeze validation-only managed oracles after player-only recovery and IL checks."""
    oracle_root = directory / "validation-oracles"
    if oracle_root.exists() or oracle_root.is_symlink():
        raise ValueError("Managed oracle snapshot directory already exists")
    for reference in reference_directories:
        resolved = Path(reference).resolve()
        if oracle_root.resolve().is_relative_to(resolved) or resolved.is_relative_to(oracle_root.resolve()):
            raise ValueError("Managed oracle snapshots must remain outside reference directories")

    sources = {
        "stripped": managed_oracle(baseline, assembly),
        "unstripped": baseline / "project/Library/ScriptAssemblies" / (assembly + ".dll"),
    }
    snapshots = {}
    for kind, source in sources.items():
        if not owned_snapshot_file(baseline, source):
            raise ValueError("Original " + kind + " managed oracle is missing or linked")
        source_hash = digest(source)
        target = oracle_root / kind / (assembly + ".dll")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        if digest(target) != source_hash:
            raise ValueError("Original " + kind + " managed oracle changed during snapshot")
        snapshots[kind] = {"path": str(target.relative_to(directory)), "sha256": source_hash}
    return snapshots


def checked_managed_oracle_snapshots(directory, assembly, snapshots):
    paths = {}
    for kind in ("stripped", "unstripped"):
        path = directory / "validation-oracles" / kind / (assembly + ".dll")
        record = snapshots.get(kind, {})
        if (record.get("path") != str(path.relative_to(directory)) or not owned_snapshot_file(directory, path) or
                digest(path) != record.get("sha256")):
            raise ValueError("Original " + kind + " managed oracle snapshot changed")
        paths[kind] = path
    return paths


def checked_declaration_report(directory, name, stage, oracle, candidate):
    """Authenticate a retained comparison and the exact assemblies it projected."""
    path = directory / name / "report.json"
    if (not isinstance(stage, dict) or stage.get("status") != "passed" or
            stage.get("differenceCount") != 0 or type(stage.get("differenceCount")) is not int or
            stage.get("report") != str(path) or not owned_snapshot_file(directory, path)):
        raise ValueError("Declaration comparison report is missing, linked or outside its canonical path")
    report_hash = digest(path)
    if report_hash != stage.get("reportSha256"):
        raise ValueError("Declaration comparison report changed since its receipt")
    identities = {"oracle": (oracle, stage.get("oracleSha256")),
                  "candidate": (candidate, stage.get("candidateSha256"))}
    for role, (assembly, expected) in identities.items():
        owner = directory if role == "oracle" else directory.parent
        if not owned_snapshot_file(owner, assembly) or digest(assembly) != expected:
            raise ValueError("Declaration comparison " + role + " changed since its receipt")
    report = json.loads(path.read_text(encoding="utf-8"))
    if (not isinstance(report, dict) or report.get("status") != "passed" or
            type(report.get("differenceCount")) is not int or report["differenceCount"] != 0 or
            report.get("diagnostics") != [] or report.get("differences") != []):
        raise ValueError("Retained declaration comparison did not pass its declared projection")
    for role, (assembly, expected) in identities.items():
        record = report.get(role)
        owner = directory if role == "oracle" else directory.parent
        if (not isinstance(record, dict) or record.get("path") != str(assembly) or
                record.get("sha256") != expected or not owned_snapshot_file(owner, assembly) or digest(assembly) != expected):
            raise ValueError("Retained declaration comparison " + role + " identity differs")
    counts = report["candidate"].get("counts")
    if not isinstance(counts, dict) or not _same_type_and_value(counts, stage.get("counts")):
        raise ValueError("Retained declaration comparison counts differ from its receipt")
    if not owned_snapshot_file(directory, path) or digest(path) != report_hash:
        raise ValueError("Declaration comparison report changed during authentication")
    return report


def checked_declaration_stages(directory, receipt, rebuilt_directory=None):
    """Rebind declaration evidence after all later validation subprocesses."""
    assembly = receipt["scope"]
    oracle = checked_managed_oracle_snapshots(directory, assembly, receipt["managedOracles"]["files"])["stripped"]
    candidates = {"recoveredDeclarations": directory / "recovered/UnityProject/RecoveredManaged" / (assembly + ".dll")}
    if rebuilt_directory is not None:
        candidates["rebuiltDeclarations"] = managed_oracle(rebuilt_directory, assembly)
    for name, candidate in candidates.items():
        checked_declaration_report(directory, name, receipt["stages"].get(name), oracle, candidate)


def checked_original_behavior(directory, profile, receipt):
    inventory = receipt.get("behaviorReports")
    checked_behavior_report_files(directory, inventory,
                                  ("project/Reports/editor-behavior.json", "player-behavior.json"))
    paths = {}
    for stage, name, relative in (("editor", "editorBehavior", "project/Reports/editor-behavior.json"),
                                   ("player", "playerBehavior", "player-behavior.json")):
        path = directory / relative
        if verify_behavior(path, stage, profile) != receipt["stages"][name]:
            raise ValueError("Original " + stage + " behavior changed since its independently verified gate")
        paths[stage] = path
    return paths


def snapshot_behavior_oracles(baseline, directory, profile, original):
    """Freeze observed behavior only after player-only recovery and IL checks."""
    target_root = directory / "validation-oracles/behavior"
    if (directory.is_symlink() or target_root.parent.is_symlink() or
            target_root.exists() or target_root.is_symlink()):
        raise ValueError("Original behavior snapshot directory already exists")
    snapshots = {}
    for stage, source in checked_original_behavior(baseline, profile, original).items():
        source_hash = next(item["sha256"] for item in original["behaviorReports"]
                           if item["path"] == source.relative_to(baseline).as_posix())
        target = target_root / (stage + ".json")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        if digest(source) != source_hash or digest(target) != source_hash:
            raise ValueError("Original " + stage + " behavior changed during snapshot")
        snapshots[stage] = {"path": target.relative_to(directory).as_posix(),
                            "sha256": source_hash, "gate": original["stages"][stage + "Behavior"].copy()}
    checked_behavior_oracles(directory, profile, snapshots)
    return snapshots


def checked_behavior_oracles(directory, profile, snapshots):
    if not isinstance(snapshots, dict) or set(snapshots) != {"editor", "player"}:
        raise ValueError("Original editor and player behavior snapshots are required")
    paths = {}
    for stage, record in snapshots.items():
        path = directory / "validation-oracles/behavior" / (stage + ".json")
        if (not isinstance(record, dict) or record.get("path") != path.relative_to(directory).as_posix() or
                not owned_snapshot_file(directory, path) or digest(path) != record.get("sha256") or
                verify_behavior(path, stage, profile) != record.get("gate")):
            raise ValueError("Original " + stage + " behavior snapshot changed")
        paths[stage] = path
    return paths


def compare_behavior_oracle(directory, profile, snapshots, stage, recovered):
    """Require exact typed observations, including bits outside an oracle's projection."""
    original = checked_behavior_oracles(directory, profile, snapshots)[stage]
    original_report, recovered_report = read_report(original), read_report(recovered)
    if not isinstance(original_report, dict) or not isinstance(recovered_report, dict):
        raise ValueError("Original and recovered behavior reports must be JSON objects")
    for key in ("unityVersion", "stage", "platform", "profile"):
        if not _same_type_and_value(original_report.get(key), recovered_report.get(key)):
            raise ValueError("Original and recovered " + stage + " behavior targets differ")
    if not _same_type_and_value(original_report["observations"], recovered_report.get("observations")):
        raise ValueError("Original and recovered " + stage + " behavior observations differ")
    return {"status": "passed", "observations": len(original_report["observations"]),
            "comparison": "exact typed observations against original " + stage + " snapshot",
            "originalSha256": digest(original), "recoveredSha256": digest(recovered)}


def current_source_files(directory):
    if not directory.is_dir():
        raise ValueError("Current fixture source directory is missing")
    files = {}
    for path in sorted(directory.rglob("*")):
        if path.is_symlink():
            raise ValueError("Current fixture source contains a symbolic link")
        if path.is_file() and (path.suffix in {".cs", ".asmdef", ".asmref", ".rsp"} or path.name == "link.xml"):
            files[path.relative_to(directory).as_posix()] = digest(path)
    if not any(path.endswith(".cs") for path in files):
        raise ValueError("Current fixture source contains no C# files")
    return files


def current_baseline_files(profile):
    source = FIXTURE_PROFILES[profile]["source"]
    sources = current_source_files(source)
    if profile == "arithmetic":
        return sources, current_source_files(source.parent / "Harness")
    harness = current_source_files(profile_harness_directory(profile))
    harness.update({"Editor/" + path: sha256 for path, sha256 in
                    current_source_files(source.parent / "Harness" / "Editor").items()})
    serializer = source.parent / "Harness" / "Runtime" / "ReportJson.cs"
    harness["Runtime/ReportJson.cs"] = digest(serializer)
    return sources, harness


def verify_recorded_files(recorded, expected, label):
    if (not isinstance(recorded, list) or
            any(not isinstance(item, dict) or type(item.get("path")) is not str or
                type(item.get("sha256")) is not str for item in recorded)):
        raise ValueError("Baseline " + label + " receipt is missing or malformed")
    files = {item["path"]: item["sha256"] for item in recorded}
    if len(files) != len(recorded) or files.keys() != expected.keys():
        raise ValueError("Baseline " + label + " path set differs from current sources")
    if files != expected:
        raise ValueError("Baseline " + label + " hashes differ from current sources")


def checked_baseline(directory, profile, expected_manifest_sha256=None,
                     expected_code_generation="OptimizeSpeed"):
    receipt = json.loads((directory / "receipt.json").read_text(encoding="utf-8"))
    if receipt.get("status") != "passed" or receipt.get("sourceKind") != "synthetic-baseline":
        raise ValueError("Baseline must be a successful original synthetic fixture run")
    if receipt.get("profile", "arithmetic") != profile:
        raise ValueError("Baseline fixture profile does not match the requested round trip")
    stages = receipt.get("stages", {})
    if (any(not isinstance(stages.get(name), dict) or stages[name].get("status") != "passed"
            for name in ("unityCompilation", "editorBehavior", "nativeBuild", "playerBehavior")) or
            stages["unityCompilation"].get("version") != VERSION):
        raise ValueError("Baseline must have passed every original exact-target gate")
    build = receipt["stages"]["nativeBuild"]
    expected = {"unityVersion": VERSION, "host": "WindowsEditor", "target": "StandaloneWindows64", "backend": "IL2CPP",
                "compilerConfiguration": "Release", "codeGeneration": expected_code_generation,
                "development": False, "errors": 0,
                "result": "Succeeded"}
    if any(build.get(key) != value for key, value in expected.items()):
        raise ValueError("Baseline native build does not match the required profile")
    sources, harness = current_baseline_files(profile)
    verify_recorded_files(receipt.get("sourceFiles"), sources, "fixture source")
    verify_recorded_files(receipt.get("harnessFiles"), harness, "harness source")
    recorded_manifest = receipt.get("packageManifest")
    if expected_manifest_sha256 is None:
        if recorded_manifest is not None:
            raise ValueError("Baseline has an explicit package manifest but this round trip does not")
    elif (not isinstance(recorded_manifest, dict) or
          recorded_manifest.get("provenance") != "explicit-auxiliary" or
          recorded_manifest.get("sha256") != expected_manifest_sha256 or
          digest(directory / "project/Packages/manifest.json") != expected_manifest_sha256):
        raise ValueError("Baseline package manifest does not match the explicit auxiliary input")
    if expected_manifest_sha256 is not None and (
            recorded_manifest.get("resolvedLockSha256") != resolved_package_lock_sha256(directory / "project")):
        raise ValueError("Baseline resolved package lock differs from its build receipt")
    verify_player_inputs(directory, receipt.get("playerInputs"))
    checked_original_behavior(directory, profile, receipt)
    assembly = FIXTURE_PROFILES[profile]["assembly"]
    managed_oracle(directory, assembly)
    if not owned_snapshot_file(directory, directory / "project/Library/ScriptAssemblies" / (assembly + ".dll")):
        raise ValueError("Baseline original managed oracle is missing or linked; rebuild a pruned original")
    if profile == "external-references":
        verify_external_reference_fixture(directory / "project", directory, receipt.get("externalDependencies"))
    if profile in EMBEDDED_FIXTURE_PACKAGES:
        verify_embedded_fixture_dependency(directory / "project", receipt.get("auxiliaryDependencies"), profile)
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
    parser.add_argument("--package-manifest", type=Path,
                        help="Explicit Unity Packages/manifest.json for both original and recovered builds")
    parser.add_argument("--external-reference-map", type=Path,
                        help="Explicit Unity asmdef/plug-in reference kinds for source export")
    parser.add_argument("--install-ilverify", action="store_true")
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600, help="Per child-stage deadline in seconds")
    parser.add_argument("--code-generation", choices=("OptimizeSpeed", "OptimizeSize"), default="OptimizeSpeed")
    parser.add_argument("--profile", choices=sorted(PROFILES), default="arithmetic")
    parser.add_argument("--defer-unity", action="store_true",
                        help="Prepare verified recovered source for run_roundtrip_batch; Unity gates remain pending")
    parser.add_argument("--comparison-tool-manifest", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--comparison-tool-manifest-sha256", help=argparse.SUPPRESS)
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
    if bool(args.comparison_tool_manifest) != bool(args.comparison_tool_manifest_sha256):
        parser.error("Both declaration comparer snapshot arguments are required")
    if args.comparison_tool_manifest:
        expected_manifest = directory.parent / "shared-comparer" / comparer_snapshot.MANIFEST
        if (not args.defer_unity or args.comparison_tool_manifest.absolute() != expected_manifest or
                not owned_snapshot_file(directory.parent, expected_manifest)):
            parser.error("Only an authenticated comparer owned by the current deferred Unity batch may be reused")
        comparer_snapshot.checked_batch(ROOT, directory.parent, args.profile,
                                        expected_manifest, args.comparison_tool_manifest_sha256)
    if (args.profile == "external-references" or args.profile in EMBEDDED_FIXTURE_PACKAGES) and \
            args.external_reference_map is None:
        parser.error("This fixture requires an explicit reference map")
    reference_map = args.external_reference_map.expanduser().resolve() if args.external_reference_map else None
    if reference_map is not None and (not reference_map.is_file() or
                                      not 0 < reference_map.stat().st_size <= 1024 * 1024):
        parser.error("--external-reference-map must name a nonempty file of at most 1 MiB")
    package_manifest = args.package_manifest.expanduser().resolve() if args.package_manifest else None
    if package_manifest is not None:
        if not package_manifest.is_file() or not 0 < package_manifest.stat().st_size <= 1024 * 1024:
            parser.error("--package-manifest must name a nonempty file of at most 1 MiB")
        try:
            if not isinstance(json.loads(package_manifest.read_text(encoding="utf-8")), dict):
                raise ValueError()
        except (UnicodeError, ValueError):
            parser.error("--package-manifest must contain a UTF-8 JSON object")
    directory.mkdir(parents=True)
    receipt = {"schemaVersion": 1, "status": "running", "scope": assembly, "profile": args.profile,
               "unityVersion": VERSION, "recoveryInputs": "isolated player binary and metadata only",
               "declarationFidelity": "unverified", "scriptAssetBindings": "out_of_scope",
               "stages": {}, "commands": []}
    manifest_snapshot = None
    manifest_sha256 = None
    reference_map_snapshot = None
    reference_map_sha256 = None
    if package_manifest is not None:
        auxiliary = directory / "auxiliary"
        auxiliary.mkdir()
        manifest_snapshot = auxiliary / "manifest.json"
        shutil.copyfile(package_manifest, manifest_snapshot)
        manifest_sha256 = digest(manifest_snapshot)
        receipt["packageManifest"] = {"provenance": "explicit-auxiliary", "sha256": manifest_sha256}
    if reference_map is not None:
        auxiliary = directory / "auxiliary"
        auxiliary.mkdir(exist_ok=True)
        reference_map_snapshot = auxiliary / "reference-map.json"
        shutil.copyfile(reference_map, reference_map_snapshot)
        reference_map_sha256 = digest(reference_map_snapshot)
        receipt["externalReferenceMap"] = {"provenance": "explicit-auxiliary", "sha256": reference_map_sha256}
    recovery_inputs = ["isolated player binary and metadata"]
    if manifest_snapshot is not None:
        recovery_inputs.append("explicit package manifest")
    if reference_map_snapshot is not None:
        recovery_inputs.append("explicit external reference-kind map")
    if args.profile == "external-references":
        recovery_inputs.append("synthetic managed dependency assemblies")
        recovery_inputs.append("exact-editor netstandard facade")
    if args.profile in EMBEDDED_FIXTURE_PACKAGES:
        recovery_inputs.append("synthetic embedded-package managed assembly")
    receipt["recoveryInputs"] = "; ".join(recovery_inputs)
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
                       "--profile", args.profile, "--code-generation", args.code_generation]
    if args.wine:
        fixture_command += ["--wine", args.wine]
    if args.toolchain_root:
        fixture_command += ["--toolchain-root", str(args.toolchain_root.expanduser().resolve())]
    try:
        baseline = args.baseline_run.expanduser().resolve() if args.baseline_run else directory / "baseline"
        if not args.baseline_run:
            baseline_manifest = ["--package-manifest", str(manifest_snapshot)] if manifest_snapshot else []
            run("baseline", fixture_command + baseline_manifest + ["--run-dir", str(baseline)], args.timeout * 2 + 30)
        original = checked_baseline(baseline, args.profile, manifest_sha256, args.code_generation)
        dependency_lock_sha256 = None
        if args.profile == "external-references":
            dependency_lock_sha256 = embedded_reference_lock_sha256(baseline / "project")
            receipt["embeddedPackageLockSha256"] = dependency_lock_sha256
        if args.profile in EMBEDDED_FIXTURE_PACKAGES:
            dependency_lock_sha256 = embedded_package_lock_sha256(
                baseline / "project", EMBEDDED_FIXTURE_PACKAGES[args.profile]["name"])
            receipt["embeddedPackageLockSha256"] = dependency_lock_sha256
        baseline_lock_sha256 = (original["packageManifest"]["resolvedLockSha256"]
                                if manifest_sha256 is not None else None)
        if baseline_lock_sha256 is not None:
            receipt["packageManifest"]["resolvedLockSha256"] = baseline_lock_sha256
        receipt["baselineReceipt"] = {"path": str(baseline / "receipt.json"), "sha256": digest(baseline / "receipt.json")}
        receipt["stages"]["original"] = original["stages"]

        # These are the only shipped player inputs to recovery. An optional package manifest
        # is a separate, explicit source-export input. No source, generated C++, application
        # managed oracle, debug symbols or analysis database is supplied.
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
        comparison_references = list(references)
        if args.profile == "external-references":
            extra = directory / "auxiliary" / "external-assemblies"
            extra.mkdir(parents=True)
            originals = {
                "Neutral.Package.dll": baseline / "project/Library/ScriptAssemblies/Neutral.Package.dll",
                "Neutral.Plugin.dll": baseline / "project/Assets/Plugins/Neutral.Plugin.dll",
                "netstandard.dll": args.editor.expanduser().resolve().parent / "Data/MonoBleedingEdge/lib/mono/4.8-api/Facades/netstandard.dll",
            }
            receipt["externalAssemblies"] = []
            for name, source in originals.items():
                if not source.is_file():
                    raise ValueError("A synthetic external assembly is missing from the original Unity project")
                target = extra / name
                shutil.copyfile(source, target)
                expected_hash = {
                    "Neutral.Package.dll": original["externalDependencies"]["compiledPackageSha256"],
                    "Neutral.Plugin.dll": original["externalDependencies"]["compiledPluginSha256"],
                    "netstandard.dll": digest(source),
                }[name]
                if digest(target) != expected_hash:
                    raise ValueError("Explicit external assembly does not match its recorded source")
                receipt["externalAssemblies"].append({"name": name, "sha256": digest(target),
                                                      "provenance": ("exact-editor-facade" if name == "netstandard.dll"
                                                                     else "synthetic-explicit-fixture")})
            references.append(str(extra))
            il_references.append(str(extra))
            comparison_references.append(str(extra))
        if args.profile in EMBEDDED_FIXTURE_PACKAGES:
            config = EMBEDDED_FIXTURE_PACKAGES[args.profile]
            dependency_name = config["assembly"]
            for reference in references + il_references:
                folder = Path(reference)
                if folder.is_dir() and any(path.is_file() and
                        path.name.casefold() == dependency_name.casefold()
                        for path in folder.iterdir()):
                    raise ValueError("Supplied reference directories must not shadow the verified synthetic package")
            extra = directory / "auxiliary" / "external-assemblies"
            extra.mkdir(parents=True)
            source = baseline / "project/Library/ScriptAssemblies" / dependency_name
            target = extra / source.name
            shutil.copyfile(source, target)
            expected_hash = original["auxiliaryDependencies"]["compiledAssemblySha256"]
            if digest(target) != expected_hash:
                raise ValueError("Synthetic fixture package assembly changed since its original build")
            receipt["externalAssemblies"] = [{"name": source.name, "sha256": expected_hash,
                                               "provenance": "synthetic-explicit-package"}]
            references.append(str(extra))
            il_references.append(str(extra))
            comparison_references.append(str(extra))
        receipt["referenceConfiguration"] = {"sourceAndDeclarations": references, "declarations": comparison_references,
                                             "managedIl": il_references,
                                             "managedIlExplicit": args.il_reference_dir is not None}
        if any((Path(path) / (assembly + ".dll")).exists() for path in references + il_references):
            raise ValueError("Reference directories must not contain the original application assembly")
        recovered = directory / "recovered"
        recovery_command = [args.dotnet, str(tool), "--force-binary-path", str(player / PLAYER_FILES[0]),
                            "--force-metadata-path", str(player / PLAYER_FILES[1]), "--force-unity-version", VERSION,
                            "--output-as", "cs_unity", "--unity-source-assemblies", assembly,
                            "--unity-reference-dir", *references, "--strict-recovery", "--output-to", str(recovered)]
        if manifest_snapshot is not None:
            recovery_command += ["--unity-package-manifest", str(manifest_snapshot)]
        if reference_map_snapshot is not None:
            recovery_command += ["--unity-external-reference-map", str(reference_map_snapshot)]
        run("recovery", recovery_command)
        report = json.loads((recovered / "source-recovery-report.json").read_text(encoding="utf-8"))
        methods = [method for method in report["Methods"] if method["AssemblyName"] == assembly]
        expected_non_bodies = set(profile.get("noManagedBody", ()))
        actual_non_bodies = {(method["TypeName"], method["MethodName"]) for method in methods
                             if method["Disposition"] == "NoManagedBody" and not method["HasNativeBody"]}
        if (not report["AnalysisCompleted"] or len(methods) != expected_methods or
                actual_non_bodies != expected_non_bodies or
                any(method["Disposition"] != "Emitted" for method in methods
                    if (method["TypeName"], method["MethodName"]) not in expected_non_bodies)):
            raise ValueError("The selected fixture methods were not all recovered without detected degradation")
        project = recovered / "UnityProject"
        emission = json.loads((project / "source-emission-report.json").read_text(encoding="utf-8"))
        if emission["SourceGeneration"] != "generated" or emission["Diagnostics"]:
            raise ValueError("Source emission did not complete without diagnostics")
        return_metadata = [item for item in emission.get("ReturnMetadata", []) if item["Name"] == assembly]
        if (len(return_metadata) != 1 or return_metadata[0]["PlayerMethodCount"] < 1 or
                return_metadata[0]["UnknownReturnRowCount"] != return_metadata[0]["PlayerMethodCount"] or
                return_metadata[0]["UnknownReturnCustomAttributeCount"] != return_metadata[0]["PlayerMethodCount"] or
                return_metadata[0]["EvidenceSource"] != "v29-player-metadata" or
                return_metadata[0]["ReturnRowPresence"] != "UnknownInPlayer" or
                return_metadata[0]["ReturnCustomAttributes"] != "UnknownInPlayer" or
                emission["DeclarationFidelity"] != "partial" or
                not any(item.startswith("DECL001:") for item in emission.get("DeclarationDiagnostics", []))):
            raise ValueError("Source report did not preserve version-29 return metadata uncertainty")
        receipt["playerOnlyReturnMetadata"] = return_metadata[0]
        if reference_map_sha256 is not None:
            if emission.get("ExternalReferenceMapProvenance") != "explicit-auxiliary":
                raise ValueError("Source emission did not record the explicit reference map")
            if args.profile == "external-references":
                kinds = {item["Name"]: item["Kind"] for item in emission["Assemblies"][0]["ExternalReferenceKinds"]}
                if (kinds.get("Neutral.Package") != "asmdef" or
                        kinds.get("Neutral.Plugin") != "precompiled-plugin"):
                    raise ValueError("Source emission did not distinguish the two synthetic reference kinds")
                definition = json.loads((project / "Assets/Recovered/ExternalReferenceFixture/ExternalReferenceFixture.asmdef").read_text(encoding="utf-8"))
                if (definition.get("references") != ["Neutral.Package"] or
                        definition.get("precompiledReferences") != ["Neutral.Plugin.dll"] or
                        definition.get("overrideReferences") is not True):
                    raise ValueError("Generated assembly definition does not preserve explicit reference kinds")
            if args.profile in EMBEDDED_FIXTURE_PACKAGES:
                dependency = Path(EMBEDDED_FIXTURE_PACKAGES[args.profile]["assembly"]).stem
                kinds = {item["Name"]: item["Kind"] for item in emission["Assemblies"][0]["ExternalReferenceKinds"]}
                if kinds.get(dependency) != "asmdef":
                    raise ValueError("Source emission did not classify the synthetic package as an asmdef")
                definition = json.loads((project / "Assets/Recovered" / assembly / (assembly + ".asmdef"))
                                        .read_text(encoding="utf-8"))
                if definition.get("references") != [dependency]:
                    raise ValueError("Generated assembly definition lost its synthetic package reference")
        if manifest_sha256 is not None:
            if (emission.get("PackageManifestProvenance") != "explicit-auxiliary" or
                    digest(project / "Packages/manifest.json") != manifest_sha256):
                raise ValueError("Recovered project did not preserve the explicit package manifest")
            receipt["packageManifest"]["dependencyCount"] = emission["PackageDependencyCount"]
        receipt["stages"]["recovery"] = {"status": "passed", "inputMethods": report["InputMethodCount"],
                                           "selectedMethods": len(methods), "selectedEmitted": len(methods) - len(actual_non_bodies),
                                           "selectedNoManagedBody": len(actual_non_bodies), "selectedUnresolved": 0}

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
        # A zero-difference comparison validates this fixture, but does not fill the
        # return-row/attribute facts that are absent from the player-derived model.
        # Snapshot both original assemblies now so baseline cleanup during the recovered
        # Unity build cannot remove a declaration-comparison input.
        oracle_snapshots = snapshot_managed_oracles(baseline, directory, assembly,
                                                   references + il_references + comparison_references)
        receipt["managedOracles"] = {"provenance": "original-baseline-validation-only",
                                     "files": oracle_snapshots}
        receipt["behaviorOracles"] = snapshot_behavior_oracles(baseline, directory, args.profile, original)
        # Snapshot the independent comparer too, since other local work may rebuild it.
        if args.comparison_tool_manifest:
            comparer_snapshot.checked_batch(ROOT, directory.parent, args.profile,
                                            expected_manifest, args.comparison_tool_manifest_sha256)
            comparison_manifest = comparer_snapshot.copy_checked(
                ROOT, expected_manifest, args.comparison_tool_manifest_sha256, directory)
        else:
            comparison_sources = comparer_snapshot.source_files(ROOT)
            run("build-declaration-comparer", comparer_snapshot.build_command(ROOT, args.dotnet))
            comparison_manifest = comparer_snapshot.freeze(
                ROOT, (ROOT / comparer_snapshot.PROJECT).parent / "bin/Release/net10.0", directory,
                receipt["commands"][-1], comparison_sources)
        comparison_hash = digest(comparison_manifest)
        comparison_snapshot = comparer_snapshot.checked(ROOT, comparison_manifest, comparison_hash)
        comparison_directory = directory / comparer_snapshot.RUNTIME_DIRECTORY
        receipt["comparisonToolFiles"] = comparison_snapshot["files"]
        receipt["comparisonToolManifest"] = {"path": comparison_manifest.name, "sha256": comparison_hash,
                                              "provenance": "current-batch-build" if args.comparison_tool_manifest else "fresh-build"}
        def compare_declarations(name, candidate):
            oracles = checked_managed_oracle_snapshots(directory, assembly, oracle_snapshots)
            candidate_hash = digest(candidate)
            output = directory / name
            command = [args.dotnet, str(comparison_directory / "DeclarationComparer.dll"),
                       "--oracle", str(oracles["stripped"]), "--candidate", str(candidate),
                       "--unstripped", str(oracles["unstripped"]), "--output", str(output)]
            for reference in comparison_references:
                command += ["--reference-dir", reference]
            run(name, command)
            comparison = json.loads((output / "report.json").read_text(encoding="utf-8"))
            if (not isinstance(comparison, dict) or comparison.get("status") != "passed" or
                    comparison.get("differenceCount") != 0 or comparison.get("diagnostics") != []):
                raise ValueError("Declaration comparison failed; inspect its local report")
            receipt["stages"][name] = {
                "status": "passed", "scope": "comparer projection against original stripped managed declarations",
                "differenceCount": 0, "counts": comparison["candidate"]["counts"],
                "originalStrippingLosses": len(comparison["stripping"]["lostIdentities"]),
                "report": str(output / "report.json"), "reportSha256": digest(output / "report.json"),
                "oracleSha256": oracle_snapshots["stripped"]["sha256"], "candidateSha256": candidate_hash,
            }
            checked_declaration_report(directory, name, receipt["stages"][name], oracles["stripped"], candidate)

        compare_declarations("recoveredDeclarations", project / "RecoveredManaged" / (assembly + ".dll"))

        if args.defer_unity:
            receipt["batchSourceDirectory"] = str(project / "Assets/Recovered" / assembly)
            receipt["artifacts"] = [{"path": str(path.relative_to(directory)), "sha256": digest(path)}
                                    for path in sorted(recovered.rglob("*")) if path.is_file()]
            verify_snapshot_inputs(directory, receipt)
            checked_declaration_stages(directory, receipt)
            receipt["status"] = "awaiting-unity-batch"
            write_json(directory / "roundtrip.json", receipt)
            print(receipt["status"] + ": " + str(directory / "roundtrip.json"))
            return 0

        replacement = directory / "replacement"
        replacement_manifest = (["--package-manifest", str(project / "Packages/manifest.json")]
                                if manifest_sha256 is not None else [])
        run("replacement", fixture_command + replacement_manifest +
            ["--source-dir", str(project / "Assets/Recovered" / assembly),
             "--run-dir", str(replacement)], args.timeout * 2 + 30)
        rebuilt = json.loads((replacement / "receipt.json").read_text(encoding="utf-8"))
        if rebuilt["status"] != "passed" or rebuilt["sourceKind"] != "replacement-source":
            raise ValueError("Recovered-source fixture did not pass its independent gates")
        if manifest_sha256 is not None:
            replacement_packages = rebuilt.get("packageManifest", {})
            if (replacement_packages.get("sha256") != manifest_sha256 or
                    digest(replacement / "project/Packages/manifest.json") != manifest_sha256):
                raise ValueError("Fresh replacement project did not use the recovered package manifest")
            if (replacement_packages.get("resolvedLockSha256") != baseline_lock_sha256 or
                    resolved_package_lock_sha256(replacement / "project") != baseline_lock_sha256):
                raise ValueError("Original and recovered projects resolved different package locks")
        if dependency_lock_sha256 is not None:
            if args.profile == "external-references":
                original_sources = verify_external_reference_fixture(
                    baseline / "project", baseline, original.get("externalDependencies"))
                rebuilt_sources = verify_external_reference_fixture(
                    replacement / "project", replacement, rebuilt.get("externalDependencies"))
            else:
                original_sources = verify_embedded_fixture_dependency(
                    baseline / "project", original.get("auxiliaryDependencies"), args.profile)
                rebuilt_sources = verify_embedded_fixture_dependency(
                    replacement / "project", rebuilt.get("auxiliaryDependencies"), args.profile)
            if original_sources != rebuilt_sources or rebuilt_sources["embeddedPackageLockSha256"] != dependency_lock_sha256:
                raise ValueError("Original and recovered synthetic external sources, SDK or package resolution differ")
        settings = ("unityVersion", "target", "backend", "compilerConfiguration", "apiCompatibility", "stripping",
                    "codeGeneration", "stripEngineCode", "scriptingDefineSymbols", "development")
        if any(original["stages"]["nativeBuild"][key] != rebuilt["stages"]["nativeBuild"][key] for key in settings):
            raise ValueError("Original and recovered native build settings differ")
        receipt["stages"]["recovered"] = rebuilt["stages"]
        checked_original_behavior(replacement, args.profile, rebuilt)
        receipt["stages"]["behaviorComparison"] = {
            stage: compare_behavior_oracle(directory, args.profile, receipt["behaviorOracles"], stage,
                                          replacement / relative)
            for stage, relative in (("editor", "project/Reports/editor-behavior.json"),
                                    ("player", "player-behavior.json"))}
        compare_declarations("rebuiltDeclarations", managed_oracle(replacement, assembly))
        receipt["declarationFidelity"] = "passed_against_stripped_oracle"
        receipt["artifacts"] = [{"path": str(path.relative_to(directory)), "sha256": digest(path)}
                                for path in sorted(recovered.rglob("*")) if path.is_file()]
        verify_snapshot_inputs(directory, receipt)
        if manifest_sha256 is not None and digest(manifest_snapshot) != manifest_sha256:
            raise ValueError("Explicit package manifest snapshot changed during validation")
        if reference_map_sha256 is not None and digest(reference_map_snapshot) != reference_map_sha256:
            raise ValueError("Explicit external reference map changed during validation")
        for item in receipt.get("externalAssemblies", []):
            if digest(directory / "auxiliary" / "external-assemblies" / item["name"]) != item["sha256"]:
                raise ValueError("Explicit synthetic external assembly changed during validation")
        if args.profile == "external-references":
            checked_baseline(baseline, args.profile, manifest_sha256, args.code_generation)
            verify_external_reference_fixture(replacement / "project", replacement, rebuilt.get("externalDependencies"))
        if baseline_lock_sha256 is not None and resolved_package_lock_sha256(baseline / "project") != baseline_lock_sha256:
            raise ValueError("Baseline resolved package lock changed during validation")
        checked_original_behavior(replacement, args.profile, rebuilt)
        if digest(baseline / "receipt.json") != receipt["baselineReceipt"]["sha256"]:
            raise ValueError("Original baseline receipt changed during validation")
        checked_declaration_stages(directory, receipt, replacement)
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
