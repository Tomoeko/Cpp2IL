"""Small acceptance-boundary regressions; these do not claim Unity validation."""

import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest import mock

import run_fixture
import run_roundtrip


class HarnessBoundaries(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.fixture = self.root / "Fixture"
        self.fixture.mkdir()
        (self.fixture / "Fixture.cs").write_text("public class Fixture {}\n", encoding="utf-8")
        (self.fixture / "RecoveryFixture.asmdef").write_text("{}\n", encoding="utf-8")
        self.harness = self.root / "Harness"
        (self.harness / "Editor").mkdir(parents=True)
        (self.harness / "Runtime").mkdir()
        (self.harness / "Editor" / "ValidationEntry.cs").write_text("public class ValidationEntry {}\n", encoding="utf-8")
        (self.harness / "Runtime" / "ReportJson.cs").write_text("public class ReportJson {}\n", encoding="utf-8")
        fixture_profile = {**run_roundtrip.FIXTURE_PROFILES["arithmetic"], "source": self.fixture}
        profile_patch = mock.patch.dict(run_roundtrip.FIXTURE_PROFILES, {"arithmetic": fixture_profile})
        profile_patch.start()
        self.addCleanup(profile_patch.stop)

    def baseline_receipt(self):
        player_inputs = []
        for relative in run_roundtrip.PLAYER_FILES:
            path = self.root / "player-input" / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic player input")
            player_inputs.append({"path": relative, "sha256": run_roundtrip.digest(path)})
        return {
            "status": "passed", "sourceKind": "synthetic-baseline", "profile": "arithmetic",
            "stages": {"nativeBuild": {"unityVersion": run_fixture.VERSION,
                                       "target": "StandaloneWindows64", "backend": "IL2CPP",
                                       "compilerConfiguration": "Release", "codeGeneration": "OptimizeSpeed",
                                       "development": False,
                                       "errors": 0, "result": "Succeeded"},
                       "playerBehavior": {"status": "passed"}},
            "playerInputs": player_inputs,
            "sourceFiles": [{"path": relative, "sha256": run_roundtrip.digest(self.fixture / relative)}
                            for relative in ("Fixture.cs", "RecoveryFixture.asmdef")],
            "harnessFiles": [{"path": relative, "sha256": run_roundtrip.digest(self.harness / relative)}
                             for relative in ("Editor/ValidationEntry.cs", "Runtime/ReportJson.cs")],
        }

    def write_baseline_receipt(self, receipt):
        (self.root / "receipt.json").write_text(json.dumps(receipt), encoding="utf-8")

    def test_source_copy_excludes_managed_oracles(self):
        source = self.root / "source"
        source.mkdir()
        for name in ("Recovered.cs", "RecoveryFixture.asmdef", "csc.rsp", "RecoveryFixture.dll", "RecoveryFixture.pdb"):
            (source / name).write_text("synthetic test placeholder")
        manifest = run_fixture.copy_sources(source, self.root / "project")
        self.assertEqual({item["path"] for item in manifest}, {"Recovered.cs", "RecoveryFixture.asmdef", "csc.rsp"})

    def test_managed_oracle_snapshots_survive_baseline_pruning(self):
        baseline = self.root / "baseline"
        assembly = "RecoveryFixture"
        stripped = baseline / "player/RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed" / (assembly + ".dll")
        unstripped = baseline / "project/Library/ScriptAssemblies" / (assembly + ".dll")
        for path, content in ((stripped, b"stripped declarations"),
                              (unstripped, b"unstripped declarations")):
            path.parent.mkdir(parents=True)
            path.write_bytes(content)
        roundtrip = self.root / "roundtrip"
        roundtrip.mkdir()
        references = self.root / "references"
        references.mkdir()
        if os.name != "nt":
            linked_roundtrip = self.root / "linked-roundtrip"
            linked_roundtrip.mkdir()
            (linked_roundtrip / "validation-oracles").symlink_to(self.root / "missing-target")
            with self.assertRaisesRegex(ValueError, "snapshot directory already exists"):
                run_roundtrip.snapshot_managed_oracles(baseline, linked_roundtrip, assembly)

        snapshots = run_roundtrip.snapshot_managed_oracles(
            baseline, roundtrip, assembly, (references,))
        self.assertEqual(set(snapshots), {"stripped", "unstripped"})
        self.assertTrue(all(item["path"].startswith("validation-oracles/")
                            for item in snapshots.values()))
        shutil.rmtree(baseline / "project")
        shutil.rmtree(baseline / "player")
        paths = run_roundtrip.checked_managed_oracle_snapshots(roundtrip, assembly, snapshots)
        self.assertEqual(paths["stripped"].read_bytes(), b"stripped declarations")
        self.assertEqual(paths["unstripped"].read_bytes(), b"unstripped declarations")
        if os.name != "nt":
            outside = self.root / "outside-managed.dll"
            outside.write_bytes(paths["stripped"].read_bytes())
            paths["stripped"].unlink()
            paths["stripped"].symlink_to(outside)
            with self.assertRaisesRegex(ValueError, "oracle snapshot changed"):
                run_roundtrip.checked_managed_oracle_snapshots(roundtrip, assembly, snapshots)
            paths["stripped"].unlink()
            paths["stripped"].write_bytes(b"stripped declarations")

        paths["stripped"].write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "oracle snapshot changed"):
            run_roundtrip.checked_managed_oracle_snapshots(roundtrip, assembly, snapshots)

    def test_player_copy_excludes_source_symbols_and_backup_assemblies(self):
        player = self.root / "player"
        files = ["RecoveryFixture.exe", "GameAssembly.dll", "UnityPlayer.dll",
                 "RecoveryFixture_Data/il2cpp_data/Metadata/global-metadata.dat",
                 "GameAssembly.pdb", "source.cs",
                 "RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed/RecoveryFixture.dll"]
        for name in files:
            target = player / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b"synthetic test placeholder")
        manifest = run_fixture.isolate_player(player, self.root / "player-input")
        self.assertEqual({item["path"] for item in manifest}, set(files[:4]))

    def test_empty_observations_cannot_pass(self):
        report = self.root / "behavior.json"
        report.write_text(json.dumps({"unityVersion": run_fixture.VERSION, "stage": "editor",
                                      "platform": "WindowsEditor", "observations": []}))
        with self.assertRaisesRegex(ValueError, "independent integer oracle"):
            run_fixture.verify_behavior(report, "editor")

    def test_explicit_manifest_baseline_requires_unchanged_resolved_lock(self):
        project = self.root / "project"
        packages = project / "Packages"
        packages.mkdir(parents=True)
        manifest = packages / "manifest.json"
        manifest.write_text('{"dependencies":{"com.example.fixture":"1.0.0"}}', encoding="utf-8")
        lock = packages / "packages-lock.json"
        lock.write_text('{"dependencies":{"com.example.fixture":{"version":"1.0.0"}}}', encoding="utf-8")
        manifest_hash = run_roundtrip.digest(manifest)
        lock_hash = run_fixture.resolved_package_lock_sha256(project)

        receipt = self.baseline_receipt()
        receipt["packageManifest"] = {"provenance": "explicit-auxiliary", "sha256": manifest_hash,
                                      "resolvedLockSha256": lock_hash}
        self.write_baseline_receipt(receipt)
        self.assertEqual(run_roundtrip.checked_baseline(self.root, "arithmetic", manifest_hash), receipt)

        lock.write_text('{"dependencies":{"com.example.fixture":{"version":"1.0.1"}}}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "resolved package lock differs"):
            run_roundtrip.checked_baseline(self.root, "arithmetic", manifest_hash)

        lock.unlink()
        with self.assertRaisesRegex(ValueError, "did not produce a Unity package lock"):
            run_roundtrip.checked_baseline(self.root, "arithmetic", manifest_hash)

    def test_default_baseline_does_not_require_package_lock(self):
        receipt = self.baseline_receipt()
        self.write_baseline_receipt(receipt)
        self.assertEqual(run_roundtrip.checked_baseline(self.root, "arithmetic"), receipt)

    def test_baseline_requires_requested_code_generation(self):
        receipt = self.baseline_receipt()
        self.write_baseline_receipt(receipt)
        with self.assertRaisesRegex(ValueError, "required profile"):
            run_roundtrip.checked_baseline(self.root, "arithmetic",
                                           expected_code_generation="OptimizeSize")
        receipt["stages"]["nativeBuild"]["codeGeneration"] = "OptimizeSize"
        self.write_baseline_receipt(receipt)
        self.assertEqual(run_roundtrip.checked_baseline(
            self.root, "arithmetic", expected_code_generation="OptimizeSize"), receipt)

    def test_baseline_rejects_changed_or_added_fixture_source(self):
        receipt = self.baseline_receipt()
        self.write_baseline_receipt(receipt)
        (self.fixture / "Fixture.cs").write_text("public class ChangedFixture {}\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "fixture source hashes differ"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")
        (self.fixture / "Fixture.cs").write_text("public class Fixture {}\n", encoding="utf-8")
        (self.fixture / "New.cs").write_text("public class NewFixture {}\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "fixture source path set differs"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")

    def test_baseline_rejects_changed_or_missing_harness_source(self):
        receipt = self.baseline_receipt()
        self.write_baseline_receipt(receipt)
        shared = self.harness / "Runtime" / "ReportJson.cs"
        shared.write_text("public class ChangedReportJson {}\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "harness source hashes differ"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")
        shared.unlink()
        with self.assertRaisesRegex(ValueError, "harness source path set differs"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")

    def test_baseline_rejects_missing_source_manifest(self):
        receipt = self.baseline_receipt()
        del receipt["sourceFiles"]
        self.write_baseline_receipt(receipt)
        with self.assertRaisesRegex(ValueError, "fixture source receipt is missing"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")

    def test_profile_harness_includes_shared_editor_and_serializer(self):
        fixture = self.root / "ReferenceStoreFixture"
        fixture.mkdir()
        (fixture / "Store.cs").write_text("public class Store {}\n", encoding="utf-8")
        harness = self.root / "ReferenceStoreHarness" / "Runtime"
        harness.mkdir(parents=True)
        (harness / "BehaviorProbe.cs").write_text("public class BehaviorProbe {}\n", encoding="utf-8")
        profile = {**run_roundtrip.FIXTURE_PROFILES["reference-store"], "source": fixture}
        with mock.patch.dict(run_roundtrip.FIXTURE_PROFILES, {"reference-store": profile}):
            receipt = self.baseline_receipt()
            receipt["profile"] = "reference-store"
            receipt["sourceFiles"] = [{"path": "Store.cs", "sha256": run_roundtrip.digest(fixture / "Store.cs")}]
            receipt["harnessFiles"] = [
                {"path": "Runtime/BehaviorProbe.cs", "sha256": run_roundtrip.digest(harness / "BehaviorProbe.cs")},
                {"path": "Editor/ValidationEntry.cs",
                 "sha256": run_roundtrip.digest(self.harness / "Editor" / "ValidationEntry.cs")},
                {"path": "Runtime/ReportJson.cs",
                 "sha256": run_roundtrip.digest(self.harness / "Runtime" / "ReportJson.cs")},
            ]
            self.write_baseline_receipt(receipt)
            self.assertEqual(run_roundtrip.checked_baseline(self.root, "reference-store"), receipt)
            (self.harness / "Editor" / "ValidationEntry.cs").write_text(
                "public class ChangedValidationEntry {}\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "harness source hashes differ"):
                run_roundtrip.checked_baseline(self.root, "reference-store")

    @unittest.skipIf(os.name == "nt", "POSIX signal-exit regression")
    def test_deadline_is_not_success_when_child_handles_termination(self):
        command = [sys.executable, "-c",
                   "import signal,sys,time; signal.signal(signal.SIGTERM, lambda *_: sys.exit(0)); time.sleep(20)"]
        outcome = run_fixture.run_process(command, os.environ.copy(), self.root / "timeout.log", 1)
        self.assertEqual(outcome["exitCode"], 0)
        self.assertTrue(outcome["timedOut"])


if __name__ == "__main__":
    unittest.main()
