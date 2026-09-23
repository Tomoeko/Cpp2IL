"""Small acceptance-boundary regressions; these do not claim Unity validation."""

import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

import run_fixture


class HarnessBoundaries(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

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

    @unittest.skipIf(os.name == "nt", "POSIX signal-exit regression")
    def test_deadline_is_not_success_when_child_handles_termination(self):
        command = [sys.executable, "-c",
                   "import signal,sys,time; signal.signal(signal.SIGTERM, lambda *_: sys.exit(0)); time.sleep(20)"]
        outcome = run_fixture.run_process(command, os.environ.copy(), self.root / "timeout.log", 1)
        self.assertEqual(outcome["exitCode"], 0)
        self.assertTrue(outcome["timedOut"])


if __name__ == "__main__":
    unittest.main()
