"""Freeze a freshly built declaration comparer for reuse within one validation batch."""

import hashlib
import json
from pathlib import Path
import shutil


PROJECT = Path("Validation/DeclarationComparer/DeclarationComparer.csproj")
RUNTIME_DIRECTORY = "declaration-comparer"
MANIFEST = "declaration-comparer-manifest.json"
REQUIRED_RUNTIME = {"DeclarationComparer.dll", "DeclarationComparer.deps.json", "DeclarationComparer.runtimeconfig.json"}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def ordinary_file(root, path):
    relative = path.relative_to(root)
    current = root
    for part in ("", *relative.parts):
        current = current / part
        if current.is_symlink():
            raise ValueError("Declaration comparer evidence may not contain symbolic links")
    if not path.is_file() or path.stat().st_size == 0:
        raise ValueError("Declaration comparer evidence is missing or empty")


def source_files(repository):
    project = repository / PROJECT
    inputs = [repository / "global.json"]
    for path in project.parent.rglob("*"):
        if (path.suffix in {".cs", ".csproj", ".props", ".targets", ".config", ".json"} and
                not {"bin", "obj"}.intersection(path.relative_to(project.parent).parts)):
            inputs.append(path)
    for parent in (repository, repository / "Validation", project.parent):
        for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config"):
            path = parent / name
            if path.exists() or path.is_symlink():
                inputs.append(path)
    result = []
    for path in sorted(set(inputs)):
        ordinary_file(repository, path)
        result.append({"path": path.relative_to(repository).as_posix(), "sha256": digest(path)})
    if PROJECT.as_posix() not in {item["path"] for item in result}:
        raise ValueError("Declaration comparer project source is missing")
    return result


def runtime_files(directory):
    if not directory.is_dir() or directory.is_symlink():
        raise ValueError("Declaration comparer runtime snapshot is missing or linked")
    result = []
    for path in sorted(directory.rglob("*")):
        if path.is_symlink():
            raise ValueError("Declaration comparer runtime may not contain symbolic links")
        if path.is_file():
            ordinary_file(directory, path)
            result.append({"path": path.relative_to(directory).as_posix(), "sha256": digest(path)})
    if not REQUIRED_RUNTIME <= {item["path"] for item in result}:
        raise ValueError("Declaration comparer runtime inventory is incomplete")
    configuration = json.loads((directory / "DeclarationComparer.runtimeconfig.json").read_text(encoding="utf-8"))
    options = configuration.get("runtimeOptions", {}) if isinstance(configuration, dict) else {}
    framework = options.get("framework", {}) if isinstance(options, dict) else {}
    if (not isinstance(framework, dict) or options.get("tfm") != "net10.0" or
            framework.get("name") != "Microsoft.NETCore.App" or
            not isinstance(framework.get("version"), str) or not framework["version"].startswith("10.0.")):
        raise ValueError("Declaration comparer runtime configuration does not match the .NET 10 build target")
    return result


def build_command(repository, dotnet):
    return [dotnet, "build", str(repository / PROJECT), "-c", "Release", "--nologo", "-v", "quiet"]


def freeze(repository, output, destination, build, sources):
    """Freeze all build outputs only after a successful build and unchanged inputs."""
    if (type(build.get("exitCode")) is not int or build["exitCode"] != 0 or build.get("timedOut") is not False or
            build.get("command", [])[1:] != build_command(repository, "dotnet")[1:]):
        raise ValueError("A fresh successful Release declaration comparer build is required")
    if source_files(repository) != sources:
        raise ValueError("Declaration comparer source changed during its build")
    files = runtime_files(output)
    runtime = destination / RUNTIME_DIRECTORY
    runtime.mkdir(parents=True)
    for item in files:
        target = runtime / item["path"]
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(output / item["path"], target)
    if runtime_files(runtime) != files or source_files(repository) != sources:
        raise ValueError("Declaration comparer build output changed while freezing it")
    manifest = destination / MANIFEST
    manifest.write_text(json.dumps({"schemaVersion": 1, "status": "passed", "configuration": "Release",
                                    "targetFramework": "net10.0", "build": build,
                                    "sourceFiles": sources, "files": files}, indent=2) + "\n", encoding="utf-8")
    return manifest


def checked(repository, manifest, expected_hash):
    """Reject stale source, incomplete runtime or an altered build receipt."""
    ordinary_file(manifest.parent, manifest)
    if digest(manifest) != expected_hash:
        raise ValueError("Declaration comparer manifest changed since its parent receipt")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError("Declaration comparer manifest is malformed")
    build = data.get("build", {})
    if (data.get("schemaVersion") != 1 or data.get("status") != "passed" or
            data.get("configuration") != "Release" or data.get("targetFramework") != "net10.0" or
            not isinstance(build, dict) or type(build.get("exitCode")) is not int or build["exitCode"] != 0 or
            build.get("timedOut") is not False or
            build.get("command", [])[1:] != build_command(repository, "dotnet")[1:]):
        raise ValueError("Declaration comparer snapshot has no successful matching build evidence")
    if data.get("sourceFiles") != source_files(repository):
        raise ValueError("Declaration comparer snapshot source identity is stale")
    if data.get("files") != runtime_files(manifest.parent / RUNTIME_DIRECTORY):
        raise ValueError("Declaration comparer runtime differs from its complete inventory")
    return data


def copy_checked(repository, manifest, expected_hash, destination):
    data = checked(repository, manifest, expected_hash)
    runtime = destination / RUNTIME_DIRECTORY
    runtime.mkdir()
    for item in data["files"]:
        target = runtime / item["path"]
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(manifest.parent / RUNTIME_DIRECTORY / item["path"], target)
    copied_manifest = destination / MANIFEST
    shutil.copyfile(manifest, copied_manifest)
    checked(repository, manifest, expected_hash)
    checked(repository, copied_manifest, expected_hash)
    return copied_manifest


def checked_batch(repository, batch, profile, manifest, expected_hash):
    """Reuse only the build authenticated by this still-running parent batch."""
    if manifest != batch / "shared-comparer" / MANIFEST:
        raise ValueError("Declaration comparer snapshot is not owned by this batch")
    ordinary_file(batch, manifest)
    parent_receipt = batch / "roundtrip.json"
    ordinary_file(batch, parent_receipt)
    receipt = json.loads(parent_receipt.read_text(encoding="utf-8"))
    if not isinstance(receipt, dict):
        raise ValueError("Declaration comparer has no running parent batch receipt")
    scope = receipt.get("scope")
    record = receipt.get("comparisonToolManifest")
    if (receipt.get("status") != "running" or not isinstance(scope, list) or
            any(type(name) is not str for name in scope) or len(set(scope)) != len(scope) or profile not in scope or
            record != {"path": manifest.relative_to(batch).as_posix(), "sha256": expected_hash}):
        raise ValueError("Declaration comparer has no matching current parent batch")
    data = checked(repository, manifest, expected_hash)
    commands = receipt.get("commands")
    if (not isinstance(commands, list) or any(not isinstance(command, dict) for command in commands) or
            [command for command in commands if command.get("stage") == "build-declaration-comparer"] != [data["build"]]):
        raise ValueError("Declaration comparer build is not authenticated by its parent batch")
    return data
