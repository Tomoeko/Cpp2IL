"""Safety checks for pruning completed local Unity build artifacts."""

import json
import os
from contextlib import redirect_stderr, redirect_stdout
from io import StringIO
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

import prune_generated_artifacts as cleanup
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

    def test_only_one_run_can_be_pruned_without_discarding_its_player_input(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "validation"
            for name in ("selected", "other"):
                run = root / name
                run.mkdir(parents=True)
                receipt = run / "receipt.json"
                receipt.write_text('{"status":"passed"}')
                old = time.time() - 48 * 3600
                os.utime(receipt, (old, old))
                for tree in ("project", "player", "player-input"):
                    (run / tree).mkdir()

            candidates = plan(root, now=time.time(), only=("selected",), retain_player_input=True)
            self.assertEqual({candidate.path.relative_to(root).as_posix() for candidate in candidates},
                             {"selected/project", "selected/player"})
            with self.assertRaises(ValueError):
                plan(root, now=time.time(), only=("../other",))

            prune(candidates, root, Path(directory) / "manifests")
            self.assertTrue((root / "selected" / "player-input").is_dir())
            self.assertTrue((root / "other" / "project").is_dir())
            self.assertTrue((root / "other" / "player").is_dir())
            self.assertTrue((root / "other" / "player-input").is_dir())

    def test_runs_scope_requires_only_and_prunes_only_selected_completed_run(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runs = root / "Files" / "runs"
            validation = root / "Files" / "validation"
            runs.mkdir(parents=True)
            validation.mkdir()
            old = time.time() - 48 * 3600
            for parent, name in ((runs, "selected"), (runs, "other"),
                                 (validation, "validation-run")):
                run = parent / name
                run.mkdir()
                receipt = run / "receipt.json"
                receipt.write_text('{"status":"passed"}')
                os.utime(receipt, (old, old))
                for tree in ("project", "player", "player-input"):
                    (run / tree).mkdir()
                    (run / tree / "generated.bin").write_bytes(b"synthetic")
            selected = runs / "selected"
            (selected / "project" / "Reports").mkdir()
            (selected / "project" / "Reports" / "result.json").write_text('{"result":"passed"}')
            (selected / "recovered").mkdir()
            (selected / "recovered" / "Recovered.cs").write_text("// keep")

            with (patch.object(cleanup, "ROOT", root),
                  patch.object(cleanup, "RUNS", runs),
                  patch.object(cleanup, "VALIDATION", validation),
                  patch.object(cleanup.subprocess, "run") as check_ignore):
                with redirect_stderr(StringIO()), self.assertRaises(SystemExit) as missing_only:
                    cleanup.main(["--runs", "--apply"])
                self.assertEqual(missing_only.exception.code, 2)
                check_ignore.assert_not_called()

                preview = StringIO()
                with redirect_stdout(preview):
                    cleanup.main(["--runs", "--only", "selected", "--show-paths",
                                  "--retain-player-input"])
                self.assertIn("selected/project", preview.getvalue())
                self.assertIn("selected/player", preview.getvalue())
                self.assertNotIn("other/project", preview.getvalue())
                self.assertTrue((selected / "project").is_dir(), "The default remains a dry run.")
                check_ignore.assert_not_called()

                check_ignore.return_value.returncode = 0
                with redirect_stdout(StringIO()):
                    cleanup.main(["--runs", "--only", "selected", "--retain-player-input",
                                  "--min-age-hours", "0", "--apply"])
                self.assertEqual([call.args[0][-1] for call in check_ignore.call_args_list],
                                 [str(runs), str(root / "Files" / "cleanup")])

            self.assertFalse((selected / "project").exists())
            self.assertFalse((selected / "player").exists())
            self.assertTrue((selected / "player-input" / "generated.bin").is_file())
            self.assertTrue((selected / "receipt.json").is_file())
            self.assertTrue((selected / "recovered" / "Recovered.cs").is_file())
            self.assertEqual((selected / "preserved-project-reports" / "result.json").read_text(),
                             '{"result":"passed"}')
            self.assertTrue((runs / "other" / "project").is_dir())
            self.assertTrue((validation / "validation-run" / "project").is_dir())
            manifests = list((root / "Files" / "cleanup").glob("prune-*.json"))
            self.assertEqual(len(manifests), 1)
            self.assertEqual({row["path"] for row in json.loads(manifests[0].read_text())["trees"]},
                             {"selected/project", "selected/player"})

    def test_runs_scope_refuses_apply_when_ignored_root_check_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runs = root / "Files" / "runs"
            run = runs / "selected"
            (run / "project").mkdir(parents=True)
            receipt = run / "receipt.json"
            receipt.write_text('{"status":"passed"}')
            old = time.time() - 48 * 3600
            os.utime(receipt, (old, old))
            with (patch.object(cleanup, "ROOT", root), patch.object(cleanup, "RUNS", runs),
                  patch.object(cleanup.subprocess, "run") as check_ignore):
                check_ignore.return_value.returncode = 1
                with redirect_stderr(StringIO()), redirect_stdout(StringIO()), self.assertRaises(SystemExit) as error:
                    cleanup.main(["--runs", "--only", "selected", "--apply"])
                self.assertEqual(error.exception.code, 2)
            self.assertTrue((run / "project").is_dir())
            self.assertFalse((root / "Files" / "cleanup").exists())


if __name__ == "__main__":
    unittest.main()
