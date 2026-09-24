"""Safety checks for pruning completed local Unity build artifacts."""

import json
import os
from pathlib import Path
import tempfile
import time
import unittest

from prune_generated_artifacts import plan, prune


class PruneGeneratedArtifactsTests(unittest.TestCase):
    def test_only_old_completed_generated_trees_are_planned(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "validation"
            root.mkdir()
            old = time.time() - 48 * 3600

            def run(name, status, age=old):
                path = root / name
                path.mkdir()
                receipt = path / "receipt.json"
                receipt.write_text(json.dumps({"status": status}))
                os.utime(receipt, (age, age))
                return path

            completed = run("completed", "passed")
            (completed / "project").mkdir()
            (completed / "project" / "temporary.bin").write_bytes(b"temporary")
            (completed / "project" / "Reports").mkdir()
            (completed / "project" / "Reports" / "editor-behavior.json").write_text('{"observations":1}')
            (completed / "replacement" / "player-input").mkdir(parents=True)
            (completed / "replacement" / "player-input" / "binary.bin").write_bytes(b"binary")
            (completed / "recovered").mkdir()
            (completed / "recovered" / "Recovered.cs").write_text("// keep")
            (completed / "run.log").write_text("keep")

            running = run("running", "running")
            (running / "project").mkdir()
            recent = run("recent", "passed", time.time())
            (recent / "project").mkdir()
            unmarked = root / "unmarked"
            (unmarked / "project").mkdir(parents=True)
            outside = Path(directory) / "outside"
            outside.mkdir()
            (outside / "important.bin").write_bytes(b"keep")
            (completed / "player").symlink_to(outside, target_is_directory=True)

            candidates = plan(root, now=time.time())
            self.assertEqual({candidate.path.relative_to(root).as_posix() for candidate in candidates},
                             {"completed/project", "completed/replacement/player-input"})
            manifest = prune(candidates, root, Path(directory) / "manifests")
            self.assertFalse((completed / "project").exists())
            self.assertFalse((completed / "replacement" / "player-input").exists())
            self.assertTrue((completed / "receipt.json").is_file())
            self.assertTrue((completed / "recovered" / "Recovered.cs").is_file())
            self.assertTrue((completed / "run.log").is_file())
            self.assertEqual((completed / "preserved-project-reports" / "editor-behavior.json").read_text(),
                             '{"observations":1}')
            self.assertTrue((running / "project").is_dir())
            self.assertTrue((recent / "project").is_dir())
            self.assertTrue((unmarked / "project").is_dir())
            self.assertTrue((outside / "important.bin").is_file())
            self.assertEqual(json.loads(manifest.read_text())["removedTrees"], 2)

    def test_exclusions_and_changed_receipts_prevent_removal(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "validation"
            root.mkdir()
            run = root / "keep"
            run.mkdir()
            receipt = run / "roundtrip.json"
            receipt.write_text('{"status":"passed"}')
            old = time.time() - 48 * 3600
            os.utime(receipt, (old, old))
            (run / "project").mkdir()
            self.assertEqual(plan(root, now=time.time(), excluded=("keep",)), [])
            with self.assertRaises(ValueError):
                plan(root, minimum_age_seconds=float("nan"))
            candidates = plan(root, now=time.time())
            receipt.write_text('{"status":"running"}')
            with self.assertRaises(ValueError):
                prune(candidates, root, Path(directory) / "manifests")
            self.assertTrue((run / "project").is_dir())

    def test_retains_player_input_while_pruning_completed_build_trees(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "validation"
            run = root / "completed"
            run.mkdir(parents=True)
            receipt = run / "receipt.json"
            receipt.write_text('{"status":"passed"}')
            old = time.time() - 48 * 3600
            os.utime(receipt, (old, old))
            for name in ("project", "player", "player-input"):
                tree = run / name
                tree.mkdir()
                (tree / "fixture.bin").write_bytes(b"synthetic")

            candidates = plan(root, now=time.time(), retain_player_input=True)
            self.assertEqual({candidate.path.name for candidate in candidates},
                             {"project", "player"})
            prune(candidates, root, Path(directory) / "manifests")
            self.assertTrue((run / "player-input" / "fixture.bin").is_file())
            self.assertFalse((run / "project").exists())
            self.assertFalse((run / "player").exists())


if __name__ == "__main__":
    unittest.main()
