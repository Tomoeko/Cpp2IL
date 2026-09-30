"""Protect source/runtime freshness when a batch shares one freshly built comparer."""

import copy
import json
from pathlib import Path
import tempfile
import unittest

import declaration_comparer_snapshot as snapshot


class DeclarationComparerSnapshotTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.repository = self.root / "repository"
        self.project = self.repository / snapshot.PROJECT
        self.project.parent.mkdir(parents=True)
        self.project.write_text('<Project Sdk="Microsoft.NET.Sdk" />', encoding="utf-8")
        (self.project.parent / "Program.cs").write_text("class Program {}", encoding="utf-8")
        (self.repository / "global.json").write_text('{"sdk":{"version":"10.0.107"}}', encoding="utf-8")
        self.output = self.root / "build"
        self.output.mkdir()
        for name in snapshot.REQUIRED_RUNTIME | {"DeclarationComparer.pdb", "DeclarationComparer"}:
            (self.output / name).write_bytes(b"neutral synthetic build output")
        (self.output / "DeclarationComparer.runtimeconfig.json").write_text(json.dumps({
            "runtimeOptions": {"tfm": "net10.0", "framework": {"name": "Microsoft.NETCore.App", "version": "10.0.0"}}}),
            encoding="utf-8")
        dependency = self.output / "runtimes/platform/native/helper.bin"
        dependency.parent.mkdir(parents=True)
        dependency.write_bytes(b"neutral transitive runtime dependency")
        self.build = {"command": snapshot.build_command(self.repository, "dotnet"),
                      "exitCode": 0, "timedOut": False, "seconds": 1.0}
        self.sources = snapshot.source_files(self.repository)
        self.manifest = snapshot.freeze(self.repository, self.output, self.root / "batch", self.build, self.sources)
        self.hash = snapshot.digest(self.manifest)

    def check(self):
        return snapshot.checked(self.repository, self.manifest, self.hash)

    def test_shared_build_keeps_complete_runtime_and_independent_copy(self):
        data = self.check()
        self.assertIn("runtimes/platform/native/helper.bin", {item["path"] for item in data["files"]})
        destination = self.root / "profile"
        destination.mkdir()
        copied = snapshot.copy_checked(self.repository, self.manifest, self.hash, destination)
        self.assertEqual(snapshot.checked(self.repository, copied, self.hash), data)
        dependency = destination / snapshot.RUNTIME_DIRECTORY / "runtimes/platform/native/helper.bin"
        dependency.write_bytes(b"changed dependency")
        with self.assertRaisesRegex(ValueError, "inventory"):
            snapshot.checked(self.repository, copied, self.hash)
        self.check()

    def test_missing_additional_changed_empty_and_linked_runtime_reject(self):
        runtime = self.manifest.parent / snapshot.RUNTIME_DIRECTORY
        path = runtime / "DeclarationComparer.dll"
        original = path.read_bytes()
        path.unlink()
        with self.assertRaises(ValueError): self.check()
        path.write_bytes(b"")
        with self.assertRaises(ValueError): self.check()
        path.write_bytes(original + b"changed")
        with self.assertRaises(ValueError): self.check()
        path.unlink()
        path.symlink_to(self.output / path.name)
        with self.assertRaises(ValueError): self.check()
        path.unlink()
        path.write_bytes(original)
        extra = runtime / "Unrecorded.dll"
        extra.write_bytes(b"unrecorded runtime")
        with self.assertRaises(ValueError): self.check()
        extra.unlink()
        self.check()

    def test_source_sdk_or_added_build_configuration_invalidates_snapshot(self):
        for path in (self.project.parent / "Program.cs", self.repository / "global.json"):
            original = path.read_bytes()
            path.write_bytes(original + b" ")
            with self.assertRaisesRegex(ValueError, "stale"):
                self.check()
            path.write_bytes(original)
        (self.repository / "Directory.Build.props").write_text("<Project />", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "stale"):
            self.check()

    def test_failed_timed_out_wrong_configuration_or_changed_build_inputs_do_not_freeze(self):
        for key, value in (("exitCode", 1), ("exitCode", False), ("timedOut", True)):
            build = copy.deepcopy(self.build)
            build[key] = value
            with self.assertRaises(ValueError):
                snapshot.freeze(self.repository, self.output, self.root / "rejected", build, self.sources)
        build = copy.deepcopy(self.build)
        build["command"][4] = "Debug"
        with self.assertRaises(ValueError):
            snapshot.freeze(self.repository, self.output, self.root / "rejected", build, self.sources)
        (self.project.parent / "Program.cs").write_text("class Changed {}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "source changed"):
            snapshot.freeze(self.repository, self.output, self.root / "rejected", self.build, self.sources)

    def test_manifest_hash_and_success_evidence_rechecked(self):
        original = self.manifest.read_bytes()
        self.manifest.write_bytes(original + b"\n")
        with self.assertRaisesRegex(ValueError, "parent receipt"):
            self.check()
        self.manifest.write_bytes(original)
        for field, value in (("status", "failed"), ("configuration", "Debug"), ("targetFramework", "net9.0")):
            data = json.loads(original)
            data[field] = value
            self.manifest.write_text(json.dumps(data), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "build evidence"):
                snapshot.checked(self.repository, self.manifest, snapshot.digest(self.manifest))
        data = json.loads(original)
        data["build"]["exitCode"] = False
        self.manifest.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "build evidence"):
            snapshot.checked(self.repository, self.manifest, snapshot.digest(self.manifest))

    def test_incompatible_runtime_target_is_rejected_before_snapshot_creation(self):
        path = self.output / "DeclarationComparer.runtimeconfig.json"
        configuration = json.loads(path.read_text())
        configuration["runtimeOptions"]["tfm"] = "net9.0"
        path.write_text(json.dumps(configuration), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "build target"):
            snapshot.freeze(self.repository, self.output, self.root / "incompatible", self.build, self.sources)

    def test_reuse_requires_current_parent_build_evidence_and_declared_profile(self):
        batch = self.root / "owned-batch"
        build = {"stage": "build-declaration-comparer", **self.build}
        manifest = snapshot.freeze(self.repository, self.output, batch / "shared-comparer", build, self.sources)
        expected_hash = snapshot.digest(manifest)
        receipt_path = batch / "roundtrip.json"
        receipt = {"status": "running", "scope": ["neutral-profile"], "commands": [build],
                   "comparisonToolManifest": {"path": "shared-comparer/" + snapshot.MANIFEST,
                                              "sha256": expected_hash}}
        def write(value):
            receipt_path.write_text(json.dumps(value), encoding="utf-8")
        write(receipt)
        snapshot.checked_batch(self.repository, batch, "neutral-profile", manifest, expected_hash)
        for field, value in (("status", "passed"), ("scope", ["other-profile"]),
                             ("scope", ["neutral-profile", "neutral-profile"]), ("commands", []),
                             ("comparisonToolManifest", {"path": "other-manifest.json", "sha256": expected_hash})):
            changed = copy.deepcopy(receipt)
            changed[field] = value
            write(changed)
            with self.assertRaises(ValueError):
                snapshot.checked_batch(self.repository, batch, "neutral-profile", manifest, expected_hash)
        write(receipt)
        receipt_path.unlink()
        with self.assertRaises(ValueError):
            snapshot.checked_batch(self.repository, batch, "neutral-profile", manifest, expected_hash)


if __name__ == "__main__":
    unittest.main()
