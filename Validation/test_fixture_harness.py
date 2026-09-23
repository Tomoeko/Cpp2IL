"""Small acceptance-boundary regressions; these do not claim Unity validation."""

import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

import run_fixture
import run_roundtrip


class HarnessBoundaries(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

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
                                       "compilerConfiguration": "Release", "development": False,
                                       "errors": 0, "result": "Succeeded"},
                       "playerBehavior": {"status": "passed"}},
            "playerInputs": player_inputs,
        }

    def test_source_copy_excludes_managed_oracles(self):
        source = self.root / "source"
        source.mkdir()
        for name in ("Recovered.cs", "RecoveryFixture.asmdef", "csc.rsp", "RecoveryFixture.dll", "RecoveryFixture.pdb"):
            (source / name).write_text("synthetic test placeholder")
        manifest = run_fixture.copy_sources(source, self.root / "project")
        self.assertEqual({item["path"] for item in manifest}, {"Recovered.cs", "RecoveryFixture.asmdef", "csc.rsp"})

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
        (self.root / "receipt.json").write_text(json.dumps(receipt), encoding="utf-8")
        self.assertEqual(run_roundtrip.checked_baseline(self.root, "arithmetic", manifest_hash), receipt)

        lock.write_text('{"dependencies":{"com.example.fixture":{"version":"1.0.1"}}}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "resolved package lock differs"):
            run_roundtrip.checked_baseline(self.root, "arithmetic", manifest_hash)

        lock.unlink()
        with self.assertRaisesRegex(ValueError, "did not produce a Unity package lock"):
            run_roundtrip.checked_baseline(self.root, "arithmetic", manifest_hash)

    def test_default_baseline_does_not_require_package_lock(self):
        receipt = self.baseline_receipt()
        (self.root / "receipt.json").write_text(json.dumps(receipt), encoding="utf-8")
        self.assertEqual(run_roundtrip.checked_baseline(self.root, "arithmetic"), receipt)

    @unittest.skipIf(os.name == "nt", "POSIX signal-exit regression")
    def test_deadline_is_not_success_when_child_handles_termination(self):
        command = [sys.executable, "-c",
                   "import signal,sys,time; signal.signal(signal.SIGTERM, lambda *_: sys.exit(0)); time.sleep(20)"]
        outcome = run_fixture.run_process(command, os.environ.copy(), self.root / "timeout.log", 1)
        self.assertEqual(outcome["exitCode"], 0)
        self.assertTrue(outcome["timedOut"])


if __name__ == "__main__":
    unittest.main()
