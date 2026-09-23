#!/usr/bin/env python3
"""Compile/build a synthetic or recovered fixture in an isolated exact-version project."""

import argparse
import array_access
import array_call
import reference_field
import exception_regions
import field_guard
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time

import byte_fields
import float_comparison
import integer_extensions
import loop_calls
import scalar_truncation
import word_fields


VERSION = "2021.3.35f1"
ROOT = Path(__file__).resolve().parent.parent
VALIDATION = ROOT / "Validation"
VALUES = [-(2**31), -(2**31) + 1, -17, -1, 0, 1, 17, 2**31 - 2, 2**31 - 1]
PROFILES = {
    "exception-regions": {"assembly": "ExceptionRegionFixture", "source": VALIDATION / "ExceptionRegionFixture", "methods": 2},
    "array-access": {"assembly": "ArrayAccessFixture", "source": VALIDATION / "ArrayAccessFixture", "methods": 8},
    "array-call": {"assembly": "ArrayCallFixture", "source": VALIDATION / "ArrayCallFixture", "methods": 6},
    "reference-field": {"assembly": "ReferenceFieldFixture", "source": VALIDATION / "ReferenceFieldFixture", "methods": 25},
    "field-guard": {"assembly": "FieldGuardFixture", "source": VALIDATION / "FieldGuardFixture", "methods": 14},
    "scalar-truncation": {"assembly": "ScalarTruncationFixture", "source": VALIDATION / "ScalarTruncationFixture", "methods": 2},
    "loop-calls": {"assembly": "LoopCallFixture", "source": VALIDATION / "LoopCallFixture", "methods": 4},
    "word-fields": {"assembly": "WordFieldFixture", "source": VALIDATION / "WordFieldFixture", "methods": 4},
    "integer-extensions": {"assembly": "IntegerExtensionFixture", "source": VALIDATION / "IntegerExtensionFixture", "methods": 12},
    "byte-fields": {"assembly": "ByteFieldFixture", "source": VALIDATION / "ByteFieldFixture", "methods": 4},
    "float-comparisons": {"assembly": "FloatComparisonFixture", "source": VALIDATION / "FloatComparisonFixture", "methods": 12},
    "components": {"assembly": "ComponentFixture", "source": VALIDATION / "ComponentFixture", "methods": 3},
    "metadata-literal": {"assembly": "MetadataLiteralFixture", "source": VALIDATION / "MetadataLiteralFixture", "methods": 1},
    "narrow-comparisons": {"assembly": "NarrowComparisonFixture", "source": VALIDATION / "NarrowComparisonFixture", "methods": 14},
    "division": {"assembly": "DivisionFixture", "source": VALIDATION / "DivisionFixture", "methods": 8},
    "shifts": {"assembly": "ShiftFixture", "source": VALIDATION / "ShiftFixture", "methods": 4},
    "arithmetic": {"assembly": "RecoveryFixture", "source": VALIDATION / "Fixture", "methods": 4},
    "integers": {"assembly": "IntegerFixture", "source": VALIDATION / "IntegerFixture", "methods": 8},
    "scalar-structs": {"assembly": "ScalarStructFixture", "source": VALIDATION / "ScalarStructFixture", "methods": 4},
    "scalar-structs-negative": {"assembly": "ScalarStructNegativeFixture", "source": VALIDATION / "ScalarStructNegativeFixture", "methods": 3},
}


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def int32(value):
    return (value + 2**31) % 2**32 - 2**31


def verify_behavior(path, stage, profile="arithmetic"):
    if profile == "exception-regions":
        return exception_regions.verify(path, stage, VERSION)
    if profile == "array-access":
        return array_access.verify(path, stage, VERSION)
    if profile == "array-call":
        return array_call.verify(path, stage, VERSION)
    if profile == "reference-field":
        return reference_field.verify(path, stage, VERSION)
    if profile == "field-guard":
        return field_guard.verify(path, stage, VERSION)
    if profile == "scalar-truncation":
        return scalar_truncation.verify(path, stage, VERSION)
    if profile == "loop-calls":
        return loop_calls.verify(path, stage, VERSION)
    if profile == "word-fields":
        return word_fields.verify(path, stage, VERSION)
    if profile == "integer-extensions":
        return integer_extensions.verify(path, stage, VERSION)
    if profile == "byte-fields":
        return byte_fields.verify(path, stage, VERSION)
    if profile == "float-comparisons":
        return float_comparison.verify(path, stage, VERSION)
    if profile == "components":
        return verify_component_behavior(path, stage)
    if profile == "metadata-literal":
        return verify_metadata_literal_behavior(path, stage)
    if profile == "narrow-comparisons":
        return verify_narrow_behavior(path, stage)
    if profile == "division":
        return verify_division_behavior(path, stage)
    if profile in ("scalar-structs", "scalar-structs-negative"):
        return verify_scalar_struct_behavior(path, stage, profile)
    if profile == "shifts":
        return verify_shift_behavior(path, stage)
    if profile == "integers":
        return verify_integer_behavior(path, stage)
    if profile != "arithmetic":
        raise ValueError("Unknown fixture profile")
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage:
        raise ValueError("Behavior report has the wrong version or stage")
    expected = []
    for left in VALUES:
        for right in VALUES:
            total = int32(left + right)
            expected.append({"left": left, "right": right, "add": total,
                             "select": int32(right - left if left < right else left + right),
                             "accumulated": total, "stored": total})
    if report["observations"] != expected:
        raise ValueError("Behavior differs from the independent integer oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "scope": "finite integer vectors; not a whole-program equivalence proof"}


def integer_observations():
    # Python integers retain every bit of UInt64 JSON numbers, including values above 2**53.
    for width in (32, 64):
        values = [0, 1, 2**(width - 1) - 1, 2**(width - 1), 2**width - 1]
        for left in values:
            for right in values:
                yield {"width": width, "left": left, "right": right,
                       "less": left < right, "greater": left > right,
                       "lessOrEqual": left <= right, "greaterOrEqual": left >= right}


def verify_integer_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != "integers":
        raise ValueError("Integer behavior report has the wrong version, stage or profile")
    observations = report["observations"]
    if not isinstance(observations, list) or any(not isinstance(item, dict) for item in observations):
        raise ValueError("Integer behavior observations must be a list of objects")
    # Avoid Python's permissive 1 == True and float/integer equality accepting a malformed report.
    for observation in observations:
        if any(type(observation.get(key)) is not int for key in ("width", "left", "right")):
            raise ValueError("Integer observation operands must be exact JSON integers")
        if any(type(observation.get(key)) is not bool for key in ("less", "greater", "lessOrEqual", "greaterOrEqual")):
            raise ValueError("Integer comparison observations must be JSON booleans")
    expected = list(integer_observations())
    if observations != expected:
        raise ValueError("Behavior differs from the independent unsigned integer oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "predicateChecks": len(expected) * 4,
            "methods": 8, "platform": report["platform"], "profile": "integers",
            "scope": "finite UInt32/UInt64 comparison vectors; not a whole-program equivalence proof"}


def division_observations():
    for width in (32, 64):
        minimum, maximum = -(2**(width - 1)), 2**(width - 1) - 1
        for signed in (True, False):
            values = ([minimum, minimum + 1, -17, -2, -1, 0, 1, 2, 17, maximum] if signed
                      else [0, 1, 2, maximum, maximum + 1, 2**width - 2, 2**width - 1])
            for left in values:
                for right in values:
                    if right == 0:
                        quotient, remainder = 0, 0
                    elif signed and left == minimum and right == -1:
                        quotient, remainder = minimum, 0
                    else:
                        # CLR integer division truncates toward zero. Never use a float:
                        # UInt64 boundary operands cannot all be represented exactly by one.
                        quotient = abs(left) // abs(right)
                        if (left < 0) != (right < 0):
                            quotient = -quotient
                        remainder = left - quotient * right
                    yield {"width": width, "signed": signed, "left": left, "right": right,
                           "quotient": quotient, "remainder": remainder}


def verify_division_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != "division":
        raise ValueError("Division report has the wrong version, stage or profile")
    observations = report["observations"]
    if not isinstance(observations, list) or any(not isinstance(item, dict) for item in observations):
        raise ValueError("Division observations must be a list of objects")
    for observation in observations:
        if (any(type(observation.get(key)) is not int for key in ("width", "left", "right", "quotient", "remainder"))
                or type(observation.get("signed")) is not bool):
            raise ValueError("Division observations require exact JSON integers and a signedness boolean")
    expected = list(division_observations())
    if observations != expected:
        raise ValueError("Behavior differs from the independent division oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "resultChecks": len(expected) * 2,
            "methods": 8, "platform": report["platform"], "profile": "division",
            "scope": "finite guarded integer division vectors; general division exceptions are not covered"}


def shift_observations():
    for width in (32, 64):
        values = [0, 1, 2**(width - 1) - 1, 2**(width - 1), 2**width - 1]
        for value in values:
            signed = value if value < 2**(width - 1) else value - 2**width
            for count in (-65, -64, -33, -32, -1, 0, 1, 31, 32, 33, 63, 64, 65):
                masked = count & (width - 1)
                yield {"width": width, "value": value, "count": count,
                       "arithmetic": signed >> masked, "logical": value >> masked}


def verify_shift_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != "shifts":
        raise ValueError("Shift report has the wrong version, stage or profile")
    observations = report["observations"]
    if not isinstance(observations, list) or any(not isinstance(item, dict) for item in observations):
        raise ValueError("Shift observations must be a list of objects")
    for observation in observations:
        if any(type(observation.get(key)) is not int for key in ("width", "value", "count", "arithmetic", "logical")):
            raise ValueError("Shift observations require exact JSON integers")
    expected = list(shift_observations())
    if observations != expected:
        raise ValueError("Behavior differs from the independent shift oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "resultChecks": len(expected) * 2,
            "methods": 4, "platform": report["platform"], "profile": "shifts",
            "scope": "finite Int32/UInt32/Int64/UInt64 shift vectors; not a whole-program equivalence proof"}


def scalar_struct_observations(profile):
    if profile == "scalar-structs":
        for item in integer_observations():
            width, left, right = item["width"], item["left"], item["right"]
            yield {"width": width, "left": left, "right": right, "equal": left == right,
                   "sum": (left + right) % 2**width}
    else:
        for case, count in (("padded", 2), ("multiple", 2), ("reference", 3)):
            for left in range(count):
                for right in range(count):
                    yield {"case": case, "left": left, "right": right, "equal": left == right}


def verify_scalar_struct_behavior(path, stage, profile):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != profile:
        raise ValueError("Scalar struct report has the wrong version, stage or profile")
    observations = report["observations"]
    if not isinstance(observations, list) or any(not isinstance(item, dict) for item in observations):
        raise ValueError("Scalar struct observations must be a list of objects")
    integer_keys = ("width", "left", "right", "sum") if profile == "scalar-structs" else ("left", "right")
    for observation in observations:
        if any(type(observation.get(key)) is not int for key in integer_keys) or type(observation.get("equal")) is not bool:
            raise ValueError("Scalar struct observations require exact JSON integers and booleans")
    expected = list(scalar_struct_observations(profile))
    if observations != expected:
        raise ValueError("Behavior differs from the independent scalar struct oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "methods": PROFILES[profile]["methods"],
            "platform": report["platform"], "profile": profile,
            "scope": "finite struct value vectors; not a whole-program equivalence proof"}


def narrow_observations():
    for condition in (False, True):
        for initial in (False, True):
            yield {"case": "boolean", "condition": condition, "initial": initial,
                   "conditionAfter": condition, "observed": initial or condition}
    for value in (0, 1, 127, 128, 255):
        yield {"case": "byte", "value": value, "zero": value == 0, "highBit": value >= 128}
    for value in (-128, -1, 0, 1, 127):
        yield {"case": "signed", "value": value, "negative": value < 0}
    for value in (0, 1, 255, 256, 65535):
        yield {"case": "word", "value": value, "zero": value == 0}
    for repeat in range(2):
        yield {"case": "metadata", "repeat": repeat, "literal": "neutral metadata literal", "typeMatches": True}
    yield {"case": "initialization", "initialCompleted": 0, "initialThrowing": 0,
           "first": 17, "second": 17, "completedCount": 1, "throwingCount": 1,
           "failures": ["TypeInitializationException/InvalidOperationException"] * 2}


def verify_narrow_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != "narrow-comparisons":
        raise ValueError("Narrow-comparison report has the wrong version, stage or profile")
    expected = list(narrow_observations())
    # Canonical JSON keeps Boolean/number types distinct and preserves exact integer spelling.
    if json.dumps(report["observations"], sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Behavior differs from the independent narrow-comparison and initialization oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "methods": 14,
            "platform": report["platform"], "profile": "narrow-comparisons",
            "scope": "finite field, metadata and initialization controls; not a whole-program equivalence proof"}


def verify_metadata_literal_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != "metadata-literal":
        raise ValueError("Metadata-literal report has the wrong version, stage or profile")
    expected = [{"repeat": repeat, "literal": "neutral metadata literal", "sameInstance": True} for repeat in range(2)]
    if json.dumps(report["observations"], sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Metadata literal observations differ from the independent oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": 2, "methods": 1,
            "platform": report["platform"], "profile": "metadata-literal",
            "scope": "one repeated literal-return method; not a whole-program equivalence proof"}


def component_observations():
    fields = [("MarkerBehaviour", "Count", "System.Int32", 0, 17),
              ("MarkerBehaviour", "caption", "System.String", None, "neutral caption"),
              ("MarkerBehaviour", "Configuration", "ComponentFixture.DataAsset", None,
               {"class": "ComponentFixture.DataAsset", "assembly": "ComponentFixture", "sameAsset": True}),
              ("MarkerBehaviour", "Payload.Value", "System.Int32", 0, -41),
              ("DataAsset", "Value", "System.Int32", 0, 73),
              ("DataAsset", "label", "System.String", None, "neutral label")]
    for repeat in range(2):
        for phase in ("fresh", "assigned"):
            for owner, path, managed_type, fresh, assigned in fields:
                yield {"repeat": repeat, "phase": phase, "class": "ComponentFixture." + owner,
                       "assembly": "ComponentFixture", "path": path, "managedType": managed_type,
                       "value": fresh if phase == "fresh" else assigned}


def verify_component_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != VERSION or report.get("stage") != stage or report.get("profile") != "components":
        raise ValueError("Component report has the wrong version, stage or profile")
    expected = list(component_observations())
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Component field observations differ from the independent initialization and assignment oracle")
    helpers = [{"input": value, "output": value} for value in (-(2**31), -1, 0, 1, 2**31 - 1)]
    if json.dumps(report.get("helpers"), sort_keys=True) != json.dumps(helpers, sort_keys=True):
        raise ValueError("Component helper observations differ from the independent identity oracle")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "helperObservations": len(helpers), "methods": 3,
            "platform": report["platform"], "profile": "components",
            "scope": "fresh runtime instances and reflected fields; no serialized asset, GUID or scene restoration"}


def copy_sources(source, destination):
    if not source.is_dir():
        raise ValueError("Source directory does not exist")
    destination.mkdir(parents=True)
    copied = []
    for item in sorted(source.rglob("*")):
        if item.is_symlink():
            raise ValueError("Fixture source may not contain symbolic links")
        if item.is_file() and (item.suffix in {".cs", ".asmdef", ".asmref", ".rsp"} or item.name == "link.xml"):
            relative = item.relative_to(source)
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(item, target)
            copied.append({"path": relative.as_posix(), "sha256": hashlib.sha256(item.read_bytes()).hexdigest()})
    if not any(item["path"].endswith(".cs") for item in copied):
        raise ValueError("No C# source found")
    return copied


def copy_harness(profile, destination):
    if profile == "arithmetic":
        return copy_sources(VALIDATION / "Harness", destination)
    harness = {"exception-regions": "ExceptionRegionHarness", "array-access": "ArrayAccessHarness", "array-call": "ArrayCallHarness", "reference-field": "ReferenceFieldHarness", "field-guard": "FieldGuardHarness", "scalar-truncation": "ScalarTruncationHarness", "loop-calls": "LoopCallHarness", "word-fields": "WordFieldHarness", "integer-extensions": "IntegerExtensionHarness", "byte-fields": "ByteFieldHarness", "float-comparisons": "FloatComparisonHarness", "components": "ComponentHarness", "metadata-literal": "MetadataLiteralHarness", "narrow-comparisons": "NarrowComparisonHarness", "division": "DivisionHarness", "shifts": "ShiftHarness", "integers": "IntegerHarness", "scalar-structs": "ScalarStructHarness",
               "scalar-structs-negative": "ScalarStructNegativeHarness"}[profile]
    copied = copy_sources(VALIDATION / harness, destination)
    for item in copy_sources(VALIDATION / "Harness" / "Editor", destination / "Editor"):
        copied.append({**item, "path": "Editor/" + item["path"]})
    serializer = VALIDATION / "Harness" / "Runtime" / "ReportJson.cs"
    shutil.copyfile(serializer, destination / "Runtime" / "ReportJson.cs")
    copied.append({"path": "Runtime/ReportJson.cs", "sha256": hashlib.sha256(serializer.read_bytes()).hexdigest()})
    return sorted(copied, key=lambda item: item["path"])


def run_process(command, environment, log, timeout, cwd=None):
    started = time.monotonic()
    timed_out = False
    with log.open("wb") as output:
        process = subprocess.Popen(command, env=environment, stdout=output, stderr=subprocess.STDOUT,
                                   start_new_session=True, cwd=cwd)
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                               stdout=output, stderr=subprocess.STDOUT, check=False)
            else:
                os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                if os.name == "nt":
                    process.kill()
                else:
                    os.killpg(process.pid, signal.SIGKILL)
                process.wait()
            code = process.returncode
    return {"command": command, "exitCode": code, "timedOut": timed_out,
            "seconds": round(time.monotonic() - started, 3)}


def isolate_player(player, destination):
    def excluded(_directory, names):
        return [name for name in names if "BackUpThisFolder" in name or "BurstDebugInformation" in name
                or Path(name).suffix.lower() in {".pdb", ".mdb", ".cs", ".cpp", ".h", ".map"}]
    shutil.copytree(player, destination, ignore=excluded)
    required = ["RecoveryFixture.exe", "GameAssembly.dll", "UnityPlayer.dll",
                "RecoveryFixture_Data/il2cpp_data/Metadata/global-metadata.dat"]
    for relative in required:
        if not (destination / relative).is_file():
            raise ValueError("Fresh player is missing required file: " + relative)
    manifest = []
    for item in sorted(destination.rglob("*")):
        if item.is_file():
            manifest.append({"path": item.relative_to(destination).as_posix(), "bytes": item.stat().st_size,
                             "sha256": hashlib.sha256(item.read_bytes()).hexdigest()})
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, required=True, help="Supplied Unity editor executable")
    parser.add_argument("--wine", help="Wine executable, required for a Windows editor on a non-Windows host")
    parser.add_argument("--toolchain-root", type=Path,
                        help="Optional VS2019 toolchain root containing VC/Tools/MSVC and Windows Kits/10")
    parser.add_argument("--profile", choices=PROFILES, default="arithmetic",
                        help="Independent selected-assembly fixture contract")
    parser.add_argument("--source-dir", type=Path,
                        help="Replacement source must expose the selected profile's API and assembly")
    parser.add_argument("--run-dir", type=Path, required=True, help="New directory under this repository's ignored Files/")
    parser.add_argument("--stage", choices=["compile", "build", "run"], default="compile",
                        help="run builds and executes; build also compiles; every invocation uses a fresh project")
    parser.add_argument("--timeout", type=int, default=600, help="Per-process deadline in seconds")
    args = parser.parse_args()
    profile = PROFILES[args.profile]
    if args.source_dir is None:
        args.source_dir = profile["source"]
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    editor = args.editor.expanduser().resolve()
    if not editor.is_file():
        parser.error("--editor must name an existing executable")
    run_dir = args.run_dir.expanduser().resolve()
    private_root = (ROOT / "Files").resolve()
    if run_dir == private_root or private_root not in run_dir.parents:
        parser.error("--run-dir must be a new child directory of the repository's Files/")
    if subprocess.run(["git", "check-ignore", "--quiet", str(run_dir)], cwd=ROOT).returncode != 0:
        parser.error("--run-dir must be gitignored")
    if run_dir.exists():
        parser.error("--run-dir already exists; select a fresh directory")
    if editor.suffix.lower() == ".exe" and os.name != "nt" and not args.wine:
        parser.error("Windows editor requires --wine on this host")
    if args.stage == "run" and os.name != "nt" and not args.wine:
        parser.error("Running a Windows player requires --wine on this host")
    environment = os.environ.copy()
    # Do not accidentally inherit a previous run's discovery override.
    environment.pop("CPP2IL_VALIDATION_TOOLCHAIN", None)
    if args.wine:
        prefix = Path.home() / ".wine_unity"
        if not prefix.is_dir():
            parser.error("The required existing Wine prefix is unavailable")
        environment["WINEPREFIX"] = str(prefix)
        environment["WINEDEBUG"] = "-all"

    def target_path(path):
        path = path.resolve()
        if not args.wine:
            return str(path)
        result = subprocess.run([args.wine, "winepath", "-w", str(path)], env=environment,
                                capture_output=True, text=True, timeout=30, check=True)
        return result.stdout.strip()

    run_dir.mkdir(parents=True)
    receipt = {"unityVersionRequired": VERSION, "requestedStage": args.stage,
               "profile": args.profile, "assembly": profile["assembly"],
               "sourceKind": "synthetic-baseline" if args.source_dir.resolve() == profile["source"].resolve() else "replacement-source",
               "status": "running", "stages": {
                   "unityCompilation": {"status": "unverified"},
                   "editorBehavior": {"status": "unverified"},
                   "nativeBuild": {"status": "unverified" if args.stage != "compile" else "not-requested"},
                   "playerBehavior": {"status": "unverified" if args.stage == "run" else "not-requested"}
               }, "commands": [],
               "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()}
    receipt_path = run_dir / "receipt.json"
    write_json(receipt_path, receipt)
    try:
        temporary = run_dir / "environment" / "tmp"
        dotnet_home = run_dir / "environment" / "dotnet-home"
        temporary.mkdir(parents=True)
        dotnet_home.mkdir()
        # Each editor and its tools receive their own scratch/cache locations.
        # Leave the parent environment and the existing Wine prefix unchanged.
        environment.update(LC_ALL="C", LANG="C", DOTNET_MULTILEVEL_LOOKUP="0",
                           DOTNET_ROLL_FORWARD="Disable", TMPDIR=str(temporary),
                           TEMP=target_path(temporary), TMP=target_path(temporary),
                           DOTNET_CLI_HOME=target_path(dotnet_home))
        receipt["launchEnvironment"] = {key: environment[key] for key in (
            "LC_ALL", "LANG", "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_ROLL_FORWARD",
            "TMPDIR", "TEMP", "TMP", "DOTNET_CLI_HOME")}
        if args.toolchain_root:
            toolchain = args.toolchain_root.expanduser().resolve()
            if not (toolchain / "VC" / "Tools" / "MSVC").is_dir() or not (toolchain / "Windows Kits" / "10").is_dir():
                raise ValueError("The toolchain root must contain VC/Tools/MSVC and Windows Kits/10")
            environment["CPP2IL_VALIDATION_TOOLCHAIN"] = target_path(toolchain)
            environment["VS160COMNTOOLS"] = target_path(toolchain / "Common7" / "Tools")
            receipt["toolchainRoot"] = str(toolchain)
            receipt["toolchainInventory"] = {
                "msvcDirectories": sorted(item.name for item in (toolchain / "VC" / "Tools" / "MSVC").iterdir() if item.is_dir()),
                "sdkIncludeDirectories": sorted(item.name for item in (toolchain / "Windows Kits" / "10" / "Include").iterdir() if item.is_dir())
            }
        project = run_dir / "project"
        (project / "ProjectSettings").mkdir(parents=True)
        (project / "Packages").mkdir()
        (project / "Reports").mkdir()
        (project / "ProjectSettings" / "ProjectVersion.txt").write_text("m_EditorVersion: " + VERSION + "\n", encoding="utf-8")
        write_json(project / "Packages" / "manifest.json", {"dependencies": {}})
        receipt["sourceFiles"] = copy_sources(args.source_dir.resolve(), project / "Assets" / profile["assembly"])
        receipt["harnessFiles"] = copy_harness(args.profile, project / "Assets" / "Validation")
        prefix_command = [args.wine, str(editor)] if args.wine else [str(editor)]
        common = prefix_command + ["-batchmode", "-nographics", "-quit", "-projectPath", target_path(project)]
        if args.stage != "compile":
            common += ["-buildTarget", "Win64"]

        def editor_stage(method, label):
            command = common + ["-executeMethod", method, "-logFile", target_path(run_dir / (label + "-editor.log"))]
            outcome = run_process(command, environment, run_dir / (label + "-process.log"), args.timeout)
            receipt["commands"].append(outcome)
            write_json(receipt_path, receipt)
            return outcome

        def behavior_stage(path, label, stage):
            try:
                receipt["stages"][label] = verify_behavior(path, stage, args.profile)
            except ValueError as error:
                receipt["stages"][label] = {"status": "failed", "reason": str(error)}
                raise

        label = "compile" if args.stage == "compile" else "build"
        method = "RecoveryValidation.ValidationEntry." + ("Compile" if args.stage == "compile" else "Build")
        outcome = editor_stage(method, label)
        marker = project / "Reports" / "compilation-complete.txt"
        if marker.read_text(encoding="utf-8").strip() != VERSION:
            raise ValueError("Fresh compilation completion marker is missing or incorrect")
        receipt["stages"]["unityCompilation"] = {"status": "passed", "version": VERSION}
        behavior_stage(project / "Reports" / "editor-behavior.json", "editorBehavior", "editor")
        if outcome["timedOut"] or outcome["exitCode"] != 0:
            raise RuntimeError(label + " editor process failed")
        if args.stage != "compile":
            build = json.loads((project / "Reports" / "build.json").read_text(encoding="utf-8"))
            expected = {"unityVersion": VERSION, "target": "StandaloneWindows64", "backend": "IL2CPP",
                        "compilerConfiguration": "Release", "development": False, "result": "Succeeded", "errors": 0}
            if any(build.get(key) != value for key, value in expected.items()):
                raise ValueError("Native build receipt does not establish the required profile")
            receipt["stages"]["nativeBuild"] = {"status": "passed", **build}
            receipt["playerInputs"] = isolate_player(run_dir / "player", run_dir / "player-input")
            if args.stage == "run":
                player = run_dir / "player-input" / "RecoveryFixture.exe"
                report_path = run_dir / "player-behavior.json"
                command = ([args.wine, str(player)] if args.wine else [str(player)]) + [
                    "-batchmode", "-nographics", "-logFile", target_path(run_dir / "player.log"),
                    "--validation-report", target_path(report_path)]
                outcome = run_process(command, environment, run_dir / "player-process.log", args.timeout)
                receipt["commands"].append(outcome)
                if outcome["timedOut"] or outcome["exitCode"] != 0:
                    raise RuntimeError("Native player process failed")
                behavior_stage(report_path, "playerBehavior", "player")
        receipt["status"] = "passed"
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError, KeyError) as error:
        receipt["status"] = "failed"
        receipt["error"] = str(error)
    finally:
        write_json(receipt_path, receipt)
    print(json.dumps({"status": receipt["status"], "stages": receipt["stages"]}, indent=2))
    return 0 if receipt["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
