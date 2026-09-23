#!/usr/bin/env python3
"""Check synthetic component script discovery in a fresh exact-version editor project."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

from run_fixture import ROOT, VERSION, copy_sources, run_process, write_json


EXPECTED = {
    "ComponentFixture.MarkerBehaviour": {"Count": ("Integer", "System.Int32"), "caption": ("String", "System.String"),
                                         "Configuration": ("ObjectReference", "ComponentFixture.DataAsset"), "Payload.Value": ("Integer", "System.Int32")},
    "ComponentFixture.DataAsset": {"Value": ("Integer", "System.Int32"), "label": ("String", "System.String")},
}


def verify_report(report, expected_discovery):
    if report.get("unityVersion") != VERSION or report.get("platform") not in {"WindowsEditor", "OSXEditor"} or report.get("apiCompatibility") != "NET_Unity_4_8":
        raise ValueError("The probe did not run in the exact supplied editor")
    if report.get("assembly") != "ComponentFixture" or report.get("assemblyAttributePreserved") is not True or report.get("ordinaryTypePreserved") is not True:
        raise ValueError("Assembly identity, attributes or ordinary types were lost")
    instances = report.get("instances", [])
    if len(instances) != len(EXPECTED) or {i.get("class") for i in instances} != set(EXPECTED):
        raise ValueError("Component instance scope is incomplete")
    for instance in instances:
        fields = instance.get("fields", [])
        if instance.get("assembly") != "ComponentFixture" or len(fields) != len(EXPECTED[instance["class"]]):
            raise ValueError("Component assembly or serialized field scope changed")
        if {f.get("path"): (f.get("kind"), f.get("managedType")) for f in fields if f.get("present") is True} != EXPECTED[instance["class"]]:
            raise ValueError("Serialized field identity changed")
    scripts = report.get("scripts", [])
    discovered = [s for s in scripts if s.get("class") in EXPECTED]
    bound = {s["class"] for s in discovered}
    if len(discovered) != len(bound):
        raise ValueError("A component has duplicate script asset mappings")
    if expected_discovery == "bound":
        if bound != set(EXPECTED):
            raise ValueError("MonoScript.GetClass did not discover every component")
        mapping = {s["class"]: s["path"] for s in discovered}
        for instance in instances:
            if instance.get("scriptClass") != instance["class"] or instance.get("scriptPath") != mapping[instance["class"]]:
                raise ValueError("Fresh instance m_Script does not map to its component's script asset")
    elif expected_discovery == "unbound" and bound:
        raise ValueError("Expected the measured single-file discovery failure")
    return {"status": "observed" if expected_discovery == "observe" else "passed", "components": len(EXPECTED),
            "discoveredComponents": len(bound), "serializedFields": sum(map(len, EXPECTED.values())), "platform": report["platform"]}


def verify_recovery_origin(source_directory, copied_directory, copied_files, receipt_path):
    receipt_path = receipt_path.resolve()
    receipt_bytes = receipt_path.read_bytes()
    origin = json.loads(receipt_bytes)
    recovery = origin.get("stages", {}).get("recovery", {})
    if (origin.get("status") != "passed" or origin.get("profile") != "components" or origin.get("scope") != "ComponentFixture" or
            origin.get("recoveryInputs") != "isolated player binary and metadata only" or recovery.get("status") != "passed" or
            recovery.get("selectedMethods") != 3 or recovery.get("selectedUnresolved") != 0):
        raise ValueError("Discovery provenance requires a passed player-only component recovery receipt")
    relative_source = "recovered/UnityProject/Assets/Recovered/ComponentFixture/"
    if source_directory.resolve() != (receipt_path.parent / relative_source).resolve():
        raise ValueError("Source directory does not belong to the supplied recovery receipt")
    source_artifacts = [item for item in origin.get("artifacts", []) if item["path"].startswith(relative_source)]
    expected = {item["path"][len(relative_source):]: item["sha256"] for item in source_artifacts}
    actual = {path.relative_to(copied_directory).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
              for path in copied_directory.rglob("*") if path.is_file() and
              (path.suffix in {".cs", ".asmdef", ".asmref", ".rsp"} or path.name == "link.xml")}
    if (not expected or len(expected) != len(source_artifacts) or len(actual) != len(copied_files) or
            set(actual) != {item["path"] for item in copied_files} or actual != expected):
        raise ValueError("Fresh copied source does not exactly match the recovery receipt artifact manifest")
    return {"path": str(receipt_path), "sha256": hashlib.sha256(receipt_bytes).hexdigest(),
            "copiedSourceFilesVerified": len(actual)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--wine")
    parser.add_argument("--source-dir", type=Path, default=ROOT / "Validation/ComponentFixture")
    parser.add_argument("--recovery-receipt", type=Path,
                        help="Passed component roundtrip receipt whose artifact hashes establish player-only source provenance")
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--expect-discovery", choices=["bound", "unbound", "observe"], default="bound")
    parser.add_argument("--timeout", type=int, default=300)
    args = parser.parse_args()
    directory = args.run_dir.resolve()
    private = (ROOT / "Files").resolve()
    if directory == private or private not in directory.parents or directory.exists() or args.timeout <= 0:
        parser.error("Use a fresh child directory under Files/ and a positive timeout")
    if subprocess.run(["git", "check-ignore", "--quiet", str(directory)], cwd=ROOT).returncode:
        parser.error("The run directory must be gitignored")
    editor = args.editor.expanduser().resolve()
    if not editor.is_file() or (editor.suffix.lower() == ".exe" and os.name != "nt" and not args.wine):
        parser.error("An existing editor is required; Windows editors require --wine on this host")
    environment = os.environ.copy()
    if args.wine:
        prefix = Path.home() / ".wine_unity"
        if not prefix.is_dir():
            parser.error("The existing licensed Wine prefix is unavailable")
        environment.update(WINEPREFIX=str(prefix), WINEDEBUG="-all")

    def target_path(path):
        if not args.wine:
            return str(path.resolve())
        return subprocess.check_output([args.wine, "winepath", "-w", str(path.resolve())], env=environment, text=True, timeout=30).strip()

    directory.mkdir(parents=True)
    project = directory / "project"
    (project / "ProjectSettings").mkdir(parents=True)
    (project / "Packages").mkdir()
    (project / "Reports").mkdir()
    (project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: " + VERSION + "\n")
    write_json(project / "Packages/manifest.json", {"dependencies": {}})
    receipt = {"status": "running", "scope": "editor discovery and fresh-instance serialized fields only",
               "recoveryProvenance": "authored synthetic fixture" if args.source_dir.resolve() == (ROOT / "Validation/ComponentFixture").resolve() else "supplied source; recovery provenance unverified",
               "nativeBuild": "not-requested", "originalGuidsAndScenes": "not-reconstructed",
               "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
               "workingTreeDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT)),
               "sourceFiles": copy_sources(args.source_dir.resolve(), project / "Assets/Fixture"),
               "probeFiles": copy_sources(ROOT / "Validation/ComponentProbe", project / "Assets/Probe")}
    serializer = ROOT / "Validation/Harness/Runtime/ReportJson.cs"
    shutil.copyfile(serializer, project / "Assets/Probe/ReportJson.cs")
    receipt["probeFiles"].append({"path": "ReportJson.cs", "sha256": hashlib.sha256(serializer.read_bytes()).hexdigest()})
    (project / "Assets/csc.rsp").write_text("-langversion:9.0\n-unsafe\n-checked-\n")
    temporary = directory / "tmp"
    temporary.mkdir()
    environment.update(LC_ALL="C", LANG="C", TMPDIR=str(temporary), TEMP=target_path(temporary), TMP=target_path(temporary))
    command = ([args.wine, str(editor)] if args.wine else [str(editor)]) + ["-batchmode", "-nographics", "-quit",
        "-projectPath", target_path(project), "-executeMethod", "ComponentProbe.ScriptProbe.Run", "-logFile", target_path(directory / "editor.log")]
    write_json(directory / "receipt.json", receipt)
    try:
        if args.recovery_receipt:
            receipt["recoveryOrigin"] = verify_recovery_origin(args.source_dir, project / "Assets/Fixture",
                                                               receipt["sourceFiles"], args.recovery_receipt)
            receipt["recoveryProvenance"] = "player-only recovered source verified against its passed recovery receipt"
        configure = list(command)
        configure[configure.index("-executeMethod") + 1] = "ComponentProbe.ScriptProbe.Configure"
        configure[configure.index("-logFile") + 1] = target_path(directory / "configure-editor.log")
        configured = run_process(configure, environment, directory / "configure-process.log", args.timeout)
        receipt["configureCommand"] = configured
        if configured["exitCode"] != 0 or configured["timedOut"]:
            raise ValueError("Editor API-profile configuration failed or timed out")
        result = run_process(command, environment, directory / "process.log", args.timeout)
        receipt["command"] = result
        if result["exitCode"] != 0 or result["timedOut"]:
            raise ValueError("Editor process failed or timed out")
        report = json.loads((project / "Reports/components.json").read_text())
        receipt["editorCheck"] = verify_report(report, args.expect_discovery)
        if args.recovery_receipt:
            receipt["recoveryOriginAfterEditor"] = verify_recovery_origin(args.source_dir, project / "Assets/Fixture",
                                                                          receipt["sourceFiles"], args.recovery_receipt)
            if receipt["recoveryOriginAfterEditor"] != receipt["recoveryOrigin"]:
                raise ValueError("Recovery provenance changed during the editor run")
        receipt["status"] = receipt["editorCheck"]["status"]
    except Exception as error:
        receipt.update(status="failed", error=str(error))
        raise
    finally:
        write_json(directory / "receipt.json", receipt)
    print("Component editor probe: " + receipt["status"])


if __name__ == "__main__":
    main()
