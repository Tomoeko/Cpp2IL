#!/usr/bin/env python3
"""Compile/build a synthetic or recovered fixture in an isolated exact-version project."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time


VERSION = "2021.3.35f1"
ROOT = Path(__file__).resolve().parent.parent
VALIDATION = ROOT / "Validation"
VALUES = [-(2**31), -(2**31) + 1, -17, -1, 0, 1, 17, 2**31 - 2, 2**31 - 1]


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def int32(value):
    return (value + 2**31) % 2**32 - 2**31


def verify_behavior(path, stage):
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


def run_process(command, environment, log, timeout):
    started = time.monotonic()
    timed_out = False
    with log.open("wb") as output:
        process = subprocess.Popen(command, env=environment, stdout=output, stderr=subprocess.STDOUT,
                                   start_new_session=True)
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
    parser.add_argument("--source-dir", type=Path, default=VALIDATION / "Fixture",
                        help="Replacement source must expose the same RecoveryFixture API and assembly")
    parser.add_argument("--run-dir", type=Path, required=True, help="New directory under this repository's ignored Files/")
    parser.add_argument("--stage", choices=["compile", "build", "run"], default="compile",
                        help="run builds and executes; build also compiles; every invocation uses a fresh project")
    parser.add_argument("--timeout", type=int, default=600, help="Per-process deadline in seconds")
    args = parser.parse_args()
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
               "sourceKind": "synthetic-baseline" if args.source_dir.resolve() == (VALIDATION / "Fixture").resolve() else "replacement-source",
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
        receipt["sourceFiles"] = copy_sources(args.source_dir.resolve(), project / "Assets" / "RecoveryFixture")
        receipt["harnessFiles"] = copy_sources(VALIDATION / "Harness", project / "Assets" / "Validation")
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
                receipt["stages"][label] = verify_behavior(path, stage)
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
