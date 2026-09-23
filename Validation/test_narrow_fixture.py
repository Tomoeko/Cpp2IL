"""Reject lost field and class-initialization effects in native fixture evidence."""

import json
from pathlib import Path
import tempfile
import unittest

import run_fixture


class NarrowFixtureBoundaryTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "narrow-comparisons", "observations": list(run_fixture.narrow_observations())}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return run_fixture.verify_behavior(self.path, "player", "narrow-comparisons")

    def test_complete_observations_pass_and_partial_evidence_does_not(self):
        self.assertEqual(self.verify()["observations"], 22)
        self.report["observations"].pop()
        with self.assertRaisesRegex(ValueError, "initialization oracle"):
            self.verify()

    def test_initial_true_field_cannot_be_replaced_by_condition(self):
        self.report["observations"][1]["observed"] = False
        with self.assertRaisesRegex(ValueError, "initialization oracle"):
            self.verify()

    def test_class_initialization_cannot_be_removed_or_repeated(self):
        for count in (0, 2):
            self.report["observations"][-1]["completedCount"] = count
            with self.assertRaisesRegex(ValueError, "initialization oracle"):
                self.verify()

    def test_failed_initialization_cannot_return_normally(self):
        self.report["observations"][-1]["failures"][0] = "no_exception"
        with self.assertRaisesRegex(ValueError, "initialization oracle"):
            self.verify()

    def test_numeric_boolean_cannot_pass(self):
        self.report["observations"][0]["conditionAfter"] = 0
        with self.assertRaisesRegex(ValueError, "initialization oracle"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
