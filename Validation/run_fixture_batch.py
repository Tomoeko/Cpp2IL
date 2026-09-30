#!/usr/bin/env python3
"""Compile and run independent fixture assemblies in one fresh exact-version player."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
from xml.etree import ElementTree

import run_fixture as fixture


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate configuration key: " + key)
        result[key] = value
    return result


def load_profiles(path):
    configuration = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
    if not isinstance(configuration, dict) or set(configuration) != {"profiles"} or \
            not isinstance(configuration["profiles"], list) or not configuration["profiles"]:
        raise ValueError("Batch configuration requires a nonempty profiles list")
    profiles, names, assemblies = [], set(), set()
    for item in configuration["profiles"]:
        if not isinstance(item, dict) or set(item) != {"profile", "sourceDirectory"}:
            raise ValueError("Each batch profile requires profile and sourceDirectory")
        name = item["profile"]
        if not isinstance(name, str) or name not in fixture.PROFILES or name in names:
            raise ValueError("Batch profiles must be known and unique")
        if name == "external-references":
            raise ValueError("The external plug-in fixture needs its individual validation runner")
        assembly = fixture.PROFILES[name]["assembly"]
        if assembly in assemblies or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.]*", assembly):
            raise ValueError("Batch fixture assembly names must be unique ordinary identifiers")
        source_value = item["sourceDirectory"]
        if not isinstance(source_value, str) or not Path(source_value).is_absolute():
            raise ValueError("Batch sourceDirectory must name an absolute directory")
        source = Path(source_value).expanduser().resolve()
        if not source.is_dir():
            raise ValueError("Batch source directory does not exist")
        # A shared project must retain the selected fixture's assembly boundary.
        definitions = list(source.rglob("*.asmdef"))
        if len(definitions) != 1 or list(source.rglob("*.asmref")):
            raise ValueError("Batch source must contain exactly one fixture assembly definition")
        definition = json.loads(definitions[0].read_text(encoding="utf-8"), object_pairs_hook=unique_object)
        if not isinstance(definition, dict) or definition.get("name") != assembly:
            raise ValueError("Batch source assembly identity differs from its profile")
        profiles.append({"profile": name, "assembly": assembly, "sourceDirectory": source})
        names.add(name)
        assemblies.add(assembly)
    return profiles


def inventory(directory):
    return [{"path": path.relative_to(directory).as_posix(), "sha256": digest(path)}
            for path in sorted(directory.rglob("*")) if path.is_file() and path.suffix != ".meta"]


def verify_inventory(directory, expected):
    if inventory(directory) != expected:
        raise ValueError("Batch source or harness inventory changed during validation")


def prepare_project(project, profiles, receipt):
    (project / "ProjectSettings").mkdir(parents=True)
    (project / "Packages").mkdir()
    (project / "Reports").mkdir()
    (project / "ProjectSettings/ProjectVersion.txt").write_text(
        "m_EditorVersion: " + fixture.VERSION + "\n", encoding="utf-8")
    fixture.write_json(project / "Packages/manifest.json", {"dependencies": {}})
    central = project / "Assets/Validation"
    fixture.copy_sources(fixture.VALIDATION / "BatchHarness", central)
    fixture.copy_sources(fixture.VALIDATION / "Harness/Editor", central / "Editor")
    shutil.copyfile(fixture.VALIDATION / "Harness/Runtime/ReportJson.cs", central / "Runtime/ReportJson.cs")
    harness_assemblies = []
    package_names = set()
    for item in profiles:
        name, assembly, source = item["profile"], item["assembly"], item["sourceDirectory"]
        record = receipt["profiles"][name]
        record["sourceFiles"] = fixture.copy_sources(source, project / "Assets" / assembly)
        record["sourceKind"] = ("synthetic-baseline" if source == fixture.PROFILES[name]["source"].resolve()
                                else "replacement-source")
        harness_source = fixture.profile_harness_directory(name)
        harness = project / "Assets/BatchHarnesses" / assembly
        original_files = fixture.copy_sources(harness_source, harness)
        definition_path = harness / "Runtime/RecoveryValidation.Runtime.asmdef"
        definition = json.loads(definition_path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
        if definition.get("name") != "RecoveryValidation.Runtime" or assembly not in definition.get("references", []):
            raise ValueError("The fixture harness does not reference its selected assembly")
        harness_assembly = "RecoveryValidation.Runtime." + assembly
        definition["name"] = harness_assembly
        # Only the central harness is visible to the unchanged editor assembly.
        definition["autoReferenced"] = False
        fixture.write_json(definition_path, definition)
        shutil.copyfile(fixture.VALIDATION / "Harness/Runtime/ReportJson.cs", harness / "Runtime/ReportJson.cs")
        record["harnessAssembly"] = harness_assembly
        record["originalHarnessFiles"] = original_files
        record["harnessFiles"] = inventory(harness)
        record["harnessAdaptation"] = "isolated assembly name and autoReferenced=false; behavior source unchanged; shared ReportJson added"
        harness_assemblies.append(harness_assembly)
        if name in fixture.EMBEDDED_FIXTURE_PACKAGES:
            package = fixture.EMBEDDED_FIXTURE_PACKAGES[name]["name"]
            if package in package_names:
                raise ValueError("Batch profiles may not install conflicting embedded packages")
            package_names.add(package)
            fixture.install_embedded_fixture_dependency(project, record, name)
    quote = lambda value: json.dumps(value, ensure_ascii=True)
    config = ("namespace RecoveryValidation\n{\n    internal static class BatchProfiles\n    {\n"
              "        internal static readonly string[] Names = new[] { " +
              ", ".join(quote(item["profile"]) for item in profiles) + " };\n"
              "        internal static readonly string[] Assemblies = new[] { " +
              ", ".join(quote(name) for name in harness_assemblies) + " };\n    }\n}\n")
    (central / "Runtime/BatchProfiles.cs").write_text(config, encoding="utf-8")
    linker = ElementTree.Element("linker")
    for assembly in harness_assemblies:
        node = ElementTree.SubElement(linker, "assembly", {"fullname": assembly})
        ElementTree.SubElement(node, "type", {"fullname": "RecoveryValidation.BehaviorProbe", "preserve": "all"})
    ElementTree.ElementTree(linker).write(central / "link.xml", encoding="utf-8", xml_declaration=True)
    receipt["batchHarnessFiles"] = inventory(central)
    receipt["packageGraph"] = {"provenance": "combined synthetic embedded fixture packages",
                               "packages": sorted(package_names), "matchesIndividualBaselineGraph": False}


def main():
    started = time.monotonic()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixtures", type=Path, required=True)
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--wine")
    parser.add_argument("--toolchain-root", type=Path)
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=180, help="Whole-batch deadline in seconds, including the editor queue")
    parser.add_argument("--code-generation", choices=("OptimizeSpeed", "OptimizeSize"), default="OptimizeSpeed")
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    deadline = started + args.timeout

    def remaining():
        seconds = deadline - time.monotonic()
        if seconds <= 0:
            raise ValueError("The whole-batch deadline expired")
        return seconds
    try:
        profiles = load_profiles(args.fixtures.expanduser().resolve())
    except (OSError, ValueError, TypeError) as error:
        parser.error(str(error))
    editor, run_dir = args.editor.expanduser().resolve(), args.run_dir.expanduser().resolve()
    private_root = (fixture.ROOT / "Files").resolve()
    if not editor.is_file():
        parser.error("--editor must name the supplied editor executable")
    if private_root not in run_dir.parents or run_dir.exists() or \
            subprocess.run(["git", "check-ignore", "--quiet", str(run_dir)], cwd=fixture.ROOT).returncode != 0:
        parser.error("--run-dir must be a fresh ignored child directory of Files/")
    if os.name != "nt" and not args.wine:
        parser.error("Running the Windows player requires --wine on this host")
    environment = os.environ.copy()
    environment.pop("CPP2IL_VALIDATION_TOOLCHAIN", None)
    environment["CPP2IL_VALIDATION_CODE_GENERATION"] = args.code_generation
    if args.wine:
        prefix = Path.home() / ".wine_unity"
        if not prefix.is_dir():
            parser.error("The required existing Wine prefix is unavailable")
        environment.update(WINEPREFIX=str(prefix), WINEDEBUG="-all")

    def target_path(path):
        path = path.resolve()
        if not args.wine:
            return str(path)
        result = subprocess.run([args.wine, "winepath", "-w", str(path)], env=environment,
                                capture_output=True, text=True, timeout=min(30, remaining()), check=True)
        return result.stdout.strip()

    run_dir.mkdir(parents=True)
    unverified = lambda: {name: {"status": "unverified"} for name in
                          ("unityCompilation", "editorBehavior", "nativeBuild", "playerBehavior")}
    receipt = {"unityVersionRequired": fixture.VERSION, "status": "running", "stages": unverified(),
               "requestedCodeGeneration": args.code_generation, "commands": [],
               "timeoutSeconds": args.timeout,
               "configurationSha256": digest(args.fixtures),
               "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=fixture.ROOT, text=True).strip(),
               "profiles": {item["profile"]: {"assembly": item["assembly"], "stages": unverified()}
                            for item in profiles}}
    receipt["validationScripts"] = [{"path": path.relative_to(fixture.ROOT).as_posix(), "sha256": digest(path)}
                                    for path in (Path(__file__).resolve(), fixture.VALIDATION / "run_fixture.py")]
    receipt_path = run_dir / "receipt.json"
    fixture.write_json(receipt_path, receipt)
    try:
        temporary, dotnet_home = run_dir / "environment/tmp", run_dir / "environment/dotnet-home"
        temporary.mkdir(parents=True)
        dotnet_home.mkdir()
        environment.update(LC_ALL="C", LANG="C", DOTNET_MULTILEVEL_LOOKUP="0", DOTNET_ROLL_FORWARD="Disable",
                           TMPDIR=str(temporary), TEMP=target_path(temporary), TMP=target_path(temporary),
                           DOTNET_CLI_HOME=target_path(dotnet_home))
        receipt["launchEnvironment"] = {key: environment[key] for key in (
            "LC_ALL", "LANG", "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_ROLL_FORWARD", "TMPDIR", "TEMP", "TMP", "DOTNET_CLI_HOME")}
        if args.toolchain_root:
            toolchain = args.toolchain_root.expanduser().resolve()
            if not (toolchain / "VC/Tools/MSVC").is_dir() or not (toolchain / "Windows Kits/10").is_dir():
                raise ValueError("The toolchain root must contain VC/Tools/MSVC and Windows Kits/10")
            environment["CPP2IL_VALIDATION_TOOLCHAIN"] = target_path(toolchain)
            environment["VS160COMNTOOLS"] = target_path(toolchain / "Common7/Tools")
            receipt["toolchainRoot"] = str(toolchain)
            receipt["toolchainInventory"] = {
                "msvcDirectories": sorted(path.name for path in (toolchain / "VC/Tools/MSVC").iterdir() if path.is_dir()),
                "sdkIncludeDirectories": sorted(path.name for path in (toolchain / "Windows Kits/10/Include").iterdir() if path.is_dir())}
        project = run_dir / "project"
        prepare_project(project, profiles, receipt)
        fixture.write_json(receipt_path, receipt)
        command = ([args.wine, str(editor)] if args.wine else [str(editor)]) + [
            "-batchmode", "-nographics", "-quit", "-projectPath", target_path(project), "-buildTarget", "Win64",
            "-executeMethod", "RecoveryValidation.ValidationEntry.Build", "-logFile", target_path(run_dir / "build-editor.log")]
        with fixture.wine_editor_slot(bool(args.wine), timeout=remaining()) as queue_seconds:
            outcome = fixture.run_process(command, environment, run_dir / "build-process.log", remaining())
        outcome["editorQueueSeconds"] = queue_seconds
        receipt["commands"].append({"stage": "build", **outcome})
        fixture.write_json(receipt_path, receipt)
        if outcome["timedOut"] or outcome["exitCode"] != 0:
            raise ValueError("Batch editor build failed or timed out")
        if (project / "Reports/compilation-complete.txt").read_text(encoding="utf-8").strip() != fixture.VERSION:
            raise ValueError("Fresh exact-version compilation marker is missing or incorrect")
        compilation = {"status": "passed", "version": fixture.VERSION}
        receipt["stages"]["unityCompilation"] = compilation
        for item in profiles:
            name, record = item["profile"], receipt["profiles"][item["profile"]]
            record["stages"]["unityCompilation"] = compilation.copy()
            record["stages"]["editorBehavior"] = fixture.verify_behavior(
                project / "Reports/batch" / name / "editor-behavior.json", "editor", name)
        receipt["stages"]["editorBehavior"] = {"status": "passed", "profiles": len(profiles)}
        build = json.loads((project / "Reports/build.json").read_text(encoding="utf-8"), object_pairs_hook=unique_object)
        expected = {"unityVersion": fixture.VERSION, "target": "StandaloneWindows64", "backend": "IL2CPP",
                    "compilerConfiguration": "Release", "codeGeneration": args.code_generation,
                    "development": False, "result": "Succeeded", "errors": 0}
        if any(build.get(key) != value for key, value in expected.items()):
            raise ValueError("Fresh native build does not establish the required exact target")
        native_build = {"status": "passed", **build}
        receipt["stages"]["nativeBuild"] = native_build
        for record in receipt["profiles"].values():
            record["stages"]["nativeBuild"] = native_build.copy()
        receipt["playerInputs"] = fixture.isolate_player(run_dir / "player", run_dir / "player-input")
        reports = run_dir / "player-behavior"
        reports.mkdir()
        command = ([args.wine, str(run_dir / "player-input/RecoveryFixture.exe")] if args.wine else
                   [str(run_dir / "player-input/RecoveryFixture.exe")]) + [
            "-batchmode", "-nographics", "-logFile", target_path(run_dir / "player.log"),
            "--validation-batch-report-directory", target_path(reports)]
        outcome = fixture.run_process(command, environment, run_dir / "player-process.log", remaining())
        receipt["commands"].append({"stage": "player", **outcome})
        if outcome["timedOut"] or outcome["exitCode"] != 0:
            raise ValueError("Fresh batch Windows player failed or timed out")
        for item in profiles:
            name, assembly = item["profile"], item["assembly"]
            record = receipt["profiles"][name]
            record["stages"]["playerBehavior"] = fixture.verify_behavior(reports / (name + ".json"), "player", name)
            verify_inventory(project / "Assets" / assembly, record["sourceFiles"])
            verify_inventory(project / "Assets/BatchHarnesses" / assembly, record["harnessFiles"])
            if name in fixture.EMBEDDED_FIXTURE_PACKAGES:
                dependency = record["auxiliaryDependencies"]
                dependency["compiledAssemblySha256"] = digest(
                    project / "Library/ScriptAssemblies" / fixture.EMBEDDED_FIXTURE_PACKAGES[name]["assembly"])
                dependency["embeddedPackageLockSha256"] = fixture.embedded_package_lock_sha256(
                    project, fixture.EMBEDDED_FIXTURE_PACKAGES[name]["name"])
                fixture.verify_embedded_fixture_dependency(project, dependency, name)
            record["behaviorReports"] = [
                {"path": (project / "Reports/batch" / name / "editor-behavior.json").relative_to(run_dir).as_posix(),
                 "sha256": digest(project / "Reports/batch" / name / "editor-behavior.json")},
                {"path": (reports / (name + ".json")).relative_to(run_dir).as_posix(), "sha256": digest(reports / (name + ".json"))}]
        verify_inventory(project / "Assets/Validation", receipt["batchHarnessFiles"])
        if digest(args.fixtures) != receipt["configurationSha256"]:
            raise ValueError("Batch configuration changed during validation")
        for script in receipt["validationScripts"]:
            if digest(fixture.ROOT / script["path"]) != script["sha256"]:
                raise ValueError("Validation scripts changed during the batch")
        for player_file in receipt["playerInputs"]:
            if digest(run_dir / "player-input" / player_file["path"]) != player_file["sha256"]:
                raise ValueError("The isolated native player changed during behavior validation")
        remaining()
        receipt["stages"]["playerBehavior"] = {"status": "passed", "profiles": len(profiles)}
        receipt["status"] = "passed"
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        receipt["status"] = "failed"
        receipt["error"] = str(error)
    finally:
        receipt["seconds"] = round(time.monotonic() - started, 3)
        fixture.write_json(receipt_path, receipt)
    print(receipt["status"] + ": " + str(receipt_path))
    return 0 if receipt["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
