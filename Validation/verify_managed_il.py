#!/usr/bin/env python3
"""Run pinned ILVerify against explicit assemblies and target references.

This checks ECMA-335 metadata and IL types, not recovered behavior or Unity
compilation. Private paths, identifiers and fingerprints stay in ignored Files/.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import uuid


ROOT = Path(__file__).resolve().parents[1]
FILES = ROOT / "Files"
TOOL_DIRECTORY = FILES / "tools" / "ilverify"
TOOL_VERSION = "10.0.7"
TOOL_PACKAGE = "dotnet-ilverify"


def require_ignored(path: Path) -> Path:
    path = path.resolve()
    if not path.is_relative_to(FILES.resolve()):
        raise ValueError("Tool and evidence output must remain inside this repository's Files directory.")
    ignored = subprocess.run(
        ["git", "check-ignore", "--quiet", "--", str(path)], cwd=ROOT, check=False
    )
    if ignored.returncode != 0:
        raise ValueError("The artifact destination is not gitignored.")
    return path


def fingerprint(path: Path) -> dict:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return {"path": str(path), "bytes": path.stat().st_size, "sha256": digest.hexdigest()}


def run_logged(command: list[str], output: Path, label: str, timeout: int, env: dict | None = None) -> dict:
    stdout_path = output / (label + ".stdout.log")
    stderr_path = output / (label + ".stderr.log")
    started = time.monotonic()
    result = {
        "command": command,
        "working_directory": str(ROOT),
        "stdout": str(stdout_path),
        "stderr": str(stderr_path),
        "exit_code": None,
        "timed_out": False,
    }
    with stdout_path.open("w", encoding="utf-8") as stdout, stderr_path.open("w", encoding="utf-8") as stderr:
        try:
            process = subprocess.run(command, cwd=ROOT, env=env, stdout=stdout, stderr=stderr,
                                     timeout=timeout, check=False)
            result["exit_code"] = process.returncode
        except subprocess.TimeoutExpired:
            result["timed_out"] = True
        except OSError as error:
            result["launch_error"] = str(error)
    result["duration_seconds"] = round(time.monotonic() - started, 3)
    return result


def install_tool(dotnet: str, output: Path, timeout: int) -> dict:
    directory = require_ignored(TOOL_DIRECTORY)
    directory.mkdir(parents=True, exist_ok=True)
    config = directory / "nuget.config"
    config.write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<configuration><packageSources><clear/>'
        '<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>'
        '</packageSources></configuration>\n', encoding="utf-8"
    )
    env = dict(os.environ)
    env["NUGET_PACKAGES"] = str(require_ignored(FILES / "nuget"))
    env["NUGET_HTTP_CACHE_PATH"] = str(directory / "http-cache")
    env["DOTNET_CLI_HOME"] = str(directory / "cli-home")
    return run_logged(
        [dotnet, "tool", "install", TOOL_PACKAGE, "--version", TOOL_VERSION,
         "--tool-path", str(directory), "--configfile", str(config)],
        output, "install", timeout, env
    )


def collect_inputs(assembly_paths: list[str], reference_directories: list[str]) -> tuple[list[Path], list[Path]]:
    assemblies = [Path(path).expanduser().resolve(strict=True) for path in assembly_paths]
    references = []
    for directory in reference_directories:
        folder = Path(directory).expanduser().resolve(strict=True)
        if not folder.is_dir():
            raise ValueError("Each reference directory must be a directory of explicit target assemblies.")
        references.extend(sorted(path.resolve() for path in folder.iterdir()
                                 if path.is_file() and path.suffix.lower() in (".dll", ".exe")))
    if len(set(assemblies)) != len(assemblies):
        raise ValueError("Each generated assembly must be selected only once.")

    # ILVerify resolves by filename without extension. Reject ambiguous versions,
    # including an original application assembly shadowing a generated assembly.
    names = {}
    for path in assemblies + references:
        if not path.is_file():
            raise ValueError("All selected assemblies must be files.")
        key = path.stem.casefold()
        previous = names.get(key)
        if previous is not None and previous != path:
            raise ValueError(f"Conflicting assembly filenames: {previous} and {path}. Supply one explicit target reference set.")
        names[key] = path
    references = sorted(set(references) - set(assemblies))
    return assemblies, references


def classify_result(execution: dict, assembly: Path) -> dict:
    stdout = Path(execution["stdout"]).read_text(encoding="utf-8", errors="replace")
    stderr = Path(execution["stderr"]).read_text(encoding="utf-8", errors="replace")
    statistics = {}
    for label, key in (("Types found", "types_found"), ("Types verified", "types_checked"),
                       ("Methods found", "methods_found"), ("Methods verified", "methods_checked")):
        matches = re.findall(r"^" + label + r": (\d+)\s*$", stdout, flags=re.MULTILINE)
        if len(matches) == 1:
            statistics[key] = int(matches[0])
    completion = f"All Classes and Methods in {assembly} Verified." in stdout
    counts_complete = len(statistics) == 4 and (
        statistics["types_found"] == statistics["types_checked"] and
        statistics["methods_found"] == statistics["methods_checked"]
    )
    missing_references = "Assembly or module not found" in stdout + stderr
    result = {"assembly": str(assembly), "execution": execution, "statistics": statistics,
              "completion_marker": completion, "status": "unverified"}
    if execution["timed_out"]:
        result["reason"] = "Verifier timed out; no complete result is available."
    elif missing_references:
        result["reason"] = "Target reference resolution is incomplete."
    elif execution["exit_code"] == 0 and completion and counts_complete:
        result["status"] = "passed"
        result["reason"] = "ILVerify completed all reported methods and types without verification errors."
    elif execution["exit_code"] == 2:
        result["status"] = "failed"
        result["reason"] = "ILVerify reported invalid or unverifiable IL/metadata; see diagnostics."
    else:
        result["reason"] = "Verifier launch, runtime, input, or completion evidence failed."
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", action="append", required=True, help="Generated managed assembly; repeat explicitly for every input.")
    parser.add_argument("--reference-dir", action="append", required=True, help="Target runtime/reference directory (nonrecursive); repeat as needed.")
    parser.add_argument("--system-module", default="mscorlib", help="Target system module simple name; Unity uses mscorlib.")
    parser.add_argument("--install-tool", action="store_true", help="Install pinned ILVerify under Files/tools/ilverify if absent.")
    parser.add_argument("--dotnet", default="dotnet", help=".NET host used only to install the tool, never as an implicit reference source.")
    parser.add_argument("--output-dir", help="New evidence directory inside ignored Files; existing paths are rejected.")
    parser.add_argument("--timeout", type=int, default=300, help="Maximum seconds per verifier/install command.")
    args = parser.parse_args()

    if args.timeout < 1:
        parser.error("--timeout must be positive")
    default_name = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex[:8]
    try:
        output = require_ignored(Path(args.output_dir) if args.output_dir else FILES / "runs" / "ilverify" / default_name)
        output.mkdir(parents=True, exist_ok=False)
    except (OSError, ValueError) as error:
        print(str(error), file=sys.stderr)
        return 2

    report = {
        "schema_version": 1,
        "status": "unverified",
        "verification": "ILVerify ECMA-335 metadata and IL type checks",
        "tool_package": TOOL_PACKAGE,
        "pinned_tool_version": TOOL_VERSION,
        "system_module": args.system_module,
        "started_utc": datetime.now(timezone.utc).isoformat(),
        "behavioral_validation": "not_run",
        "unity_compilation": "not_run",
        "assemblies": [],
        "references": [],
        "results": [],
    }
    try:
        assemblies, references = collect_inputs(args.assembly, args.reference_dir)
        if args.system_module.casefold() not in {p.stem.casefold() for p in assemblies + references}:
            raise ValueError("The selected system module is absent from the explicit input/reference set.")
        report["assemblies"] = [fingerprint(path) for path in assemblies]
        report["references"] = [fingerprint(path) for path in references]
        tool = require_ignored(TOOL_DIRECTORY / ("ilverify.exe" if os.name == "nt" else "ilverify"))
        if not tool.is_file() and args.install_tool:
            dotnet = shutil.which(args.dotnet)
            if dotnet is None:
                raise ValueError("The requested .NET host is unavailable; ILVerify was not installed.")
            report["installation"] = install_tool(dotnet, output, args.timeout)
            if report["installation"]["exit_code"] != 0:
                raise ValueError("Pinned ILVerify installation failed; see install logs.")
        if not tool.is_file():
            raise ValueError("Pinned ILVerify is unavailable; rerun with --install-tool to install it under Files/tools/ilverify.")
        version = run_logged([str(tool), "--version"], output, "version", args.timeout)
        report["tool_version_execution"] = version
        actual_version = Path(version["stdout"]).read_text(encoding="utf-8", errors="replace").strip()
        report["actual_tool_version"] = actual_version
        if version["exit_code"] != 0 or not re.match(re.escape(TOOL_VERSION) + r"(?:[-+\s]|$)", actual_version):
            raise ValueError("The installed verifier does not report the pinned version, or its runtime could not start.")

        for index, assembly in enumerate(assemblies):
            command = [str(tool), str(assembly), "--system-module", args.system_module, "--statistics", "--tokens"]
            for reference in sorted(references + [peer for peer in assemblies if peer != assembly]):
                command.extend(["--reference", str(reference)])
            execution = run_logged(command, output, f"assembly-{index:04d}", args.timeout)
            report["results"].append(classify_result(execution, assembly))
        statuses = {result["status"] for result in report["results"]}
        report["status"] = "unverified" if "unverified" in statuses else "failed" if "failed" in statuses else "passed"
        if report["assemblies"] != [fingerprint(path) for path in assemblies] or report["references"] != [fingerprint(path) for path in references]:
            report["status"] = "unverified"
            report["reason"] = "Inputs or references changed during verification; recorded evidence is not stable."
    except (OSError, ValueError) as error:
        report["reason"] = str(error)
    finally:
        report["finished_utc"] = datetime.now(timezone.utc).isoformat()
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Managed IL verification: {report['status']}. Evidence: {output / 'result.json'}")
    return {"passed": 0, "failed": 1, "unverified": 2}[report["status"]]


if __name__ == "__main__":
    raise SystemExit(main())
