"""Regressions for sparse files, storage admission and terminated child results."""

import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

import run_fixture
import storage_budget


class StorageBudgetTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def test_sparse_file_counts_logical_size(self):
        with (self.root / "sparse").open("wb") as output:
            output.truncate(storage_budget.LIMIT_BYTES)
        self.assertGreaterEqual(storage_budget.usage(self.root)["countedBytes"], storage_budget.LIMIT_BYTES)
        with self.assertRaisesRegex(ValueError, "storage budget reached"):
            storage_budget.check_headroom(self.root)

    def test_directory_symlink_does_not_count_external_contents(self):
        external = self.root / "external"
        external.mkdir()
        with (external / "large").open("wb") as output:
            output.truncate(storage_budget.LIMIT_BYTES)
        owned = self.root / "owned"
        owned.mkdir()
        try:
            (owned / "link").symlink_to(external, target_is_directory=True)
        except OSError:
            self.skipTest("Symbolic links are unavailable")
        self.assertLess(storage_budget.usage(owned)["countedBytes"], 1_000_000)

    def test_allocated_bytes_can_exceed_logical_bytes(self):
        (self.root / "small").write_bytes(b"x")
        measured = storage_budget.usage(self.root)
        self.assertEqual(measured["countedBytes"], max(measured["logicalBytes"], measured["allocatedBytes"]))
        with mock.patch.object(storage_budget, "LIMIT_BYTES", measured["countedBytes"]):
            storage_budget.check_headroom(self.root, 0)
            with self.assertRaises(ValueError):
                storage_budget.check_headroom(self.root, 1)

    def test_invalid_reserves_are_rejected(self):
        for reserve in (-1, False, 1.5):
            with self.subTest(reserve=reserve), self.assertRaises(ValueError):
                storage_budget.check_headroom(self.root, reserve)

    def test_directory_removed_during_scan_is_tolerated(self):
        with mock.patch.object(storage_budget.os, "scandir", side_effect=FileNotFoundError):
            self.assertEqual(storage_budget.usage(self.root)["logicalBytes"], 0)

    def test_over_budget_does_not_launch_child(self):
        files = self.root / "Files"
        files.mkdir()
        with mock.patch.object(run_fixture, "ROOT", self.root), \
                mock.patch.object(storage_budget, "LIMIT_BYTES", 1), \
                mock.patch.object(subprocess, "Popen") as launch:
            with self.assertRaisesRegex(ValueError, "storage budget reached"):
                run_fixture.run_process([sys.executable, "-c", "pass"], os.environ.copy(), files / "child.log", 3)
            launch.assert_not_called()

    def test_fast_success_is_rejected_if_final_storage_sample_exceeds_budget(self):
        files = self.root / "Files"
        files.mkdir()
        samples = [{"logicalBytes": 1, "allocatedBytes": 1, "countedBytes": 1,
                    "limitBytes": storage_budget.LIMIT_BYTES},
                   {"logicalBytes": storage_budget.LIMIT_BYTES, "allocatedBytes": 1,
                    "countedBytes": storage_budget.LIMIT_BYTES, "limitBytes": storage_budget.LIMIT_BYTES}]
        with mock.patch.object(run_fixture, "ROOT", self.root), \
                mock.patch.object(storage_budget, "usage", side_effect=samples):
            result = run_fixture.run_process([sys.executable, "-c", "pass"], os.environ.copy(), files / "child.log", 3)
        self.assertEqual(result["exitCode"], 0)
        self.assertFalse(result["timedOut"])
        self.assertTrue(result["storageLimitReached"])
        self.assertFalse(run_fixture.process_succeeded(result))

    @unittest.skipIf(os.name == "nt", "POSIX process-group regression")
    def test_storage_stop_is_not_success_when_child_handles_termination(self):
        files = self.root / "Files"
        files.mkdir()
        marker = files / "ready"
        command = [sys.executable, "-c",
                   "import pathlib,signal,sys,time; signal.signal(signal.SIGTERM, lambda *_: sys.exit(0)); "
                   "pathlib.Path(sys.argv[1]).write_text('ready'); time.sleep(20)", str(marker)]
        actual_usage = storage_budget.usage

        def sample(path):
            measured = actual_usage(path)
            if marker.exists():
                measured["countedBytes"] = storage_budget.LIMIT_BYTES
            return measured

        with mock.patch.object(run_fixture, "ROOT", self.root), \
                mock.patch.object(storage_budget, "POLL_SECONDS", 0.1), \
                mock.patch.object(storage_budget, "usage", side_effect=sample):
            result = run_fixture.run_process(command, os.environ.copy(), files / "child.log", 5)
        self.assertEqual(result["exitCode"], 0)
        self.assertFalse(result["timedOut"])
        self.assertTrue(result["storageLimitReached"])
        self.assertFalse(run_fixture.process_succeeded(result))

    @unittest.skipIf(os.name == "nt", "POSIX process-group regression")
    def test_storage_stop_kills_writing_descendant_after_leader_exits(self):
        self.check_writing_descendant(False)

    @unittest.skipIf(os.name == "nt", "POSIX process-group regression")
    def test_final_storage_sample_kills_writing_descendant_after_leader_exits(self):
        self.check_writing_descendant(True)

    def check_writing_descendant(self, exit_leader):
        files = self.root / "Files"
        files.mkdir()
        marker, output = files / "child-pid", files / "child-output"
        child = ("import os,pathlib,signal,sys,time; signal.signal(signal.SIGTERM, signal.SIG_IGN); "
                 "f=open(sys.argv[2],'ab',buffering=0); "
                 "pathlib.Path(sys.argv[1]).write_text(str(os.getpid()))\n"
                 "while True: f.write(b'x'); time.sleep(0.01)")
        parent = ("import pathlib,signal,subprocess,sys,time; signal.signal(signal.SIGTERM,lambda *_:sys.exit(0)); "
                  "subprocess.Popen([sys.executable,'-c',sys.argv[1],*sys.argv[2:]])\n")
        parent += ("while not pathlib.Path(sys.argv[2]).exists(): time.sleep(0.01)" if exit_leader else "time.sleep(20)")
        actual_usage = storage_budget.usage

        def sample(path):
            measured = actual_usage(path)
            if marker.exists():
                measured["countedBytes"] = storage_budget.LIMIT_BYTES
            return measured

        try:
            with mock.patch.object(run_fixture, "ROOT", self.root), \
                    mock.patch.object(storage_budget, "POLL_SECONDS", 0.1), \
                    mock.patch.object(storage_budget, "usage", side_effect=sample):
                result = run_fixture.run_process(
                    [sys.executable, "-c", parent, child, str(marker), str(output)],
                    os.environ.copy(), files / "child.log", 5)
            self.assertEqual(result["exitCode"], 0)
            self.assertTrue(result["storageLimitReached"])
            self.assertFalse(run_fixture.process_succeeded(result))
            time.sleep(0.1)
            length = output.stat().st_size
            time.sleep(0.2)
            self.assertEqual(output.stat().st_size, length, "A descendant continued writing after termination")
        finally:
            if marker.exists():
                try:
                    os.kill(int(marker.read_text()), signal.SIGKILL)
                except ProcessLookupError:
                    pass


if __name__ == "__main__":
    unittest.main()
