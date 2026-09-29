"""Reject branch, value-preservation and side-effect regressions independently."""

import json
from pathlib import Path
import tempfile
import unittest

import boolean_composition
import run_fixture


class BooleanCompositionBoundaryTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "boolean-composition", "observations": list(boolean_composition.observations())}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return boolean_composition.verify(self.path, "player", run_fixture.VERSION)

    def test_unordered_branch_preserves_value_and_has_no_side_effect(self):
        item = next(row for row in self.report["observations"]
                    if row["leftBits"] == "7fc00001" and row["rightBits"] == "3f800000")
        self.assertEqual(item["resultBits"], "3f800000")
        self.assertEqual(item["updates"], 17)
        item["updates"] = 18
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()

    def test_equal_signed_zero_preserves_the_existing_zero_sign(self):
        item = next(row for row in self.report["observations"]
                    if row["leftBits"] == "00000000" and row["rightBits"] == "80000000")
        self.assertEqual(item["resultBits"], "80000000")
        self.assertEqual(self.verify()["resultChecks"], 676)
        item["resultBits"] = "00000000"
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()

    def test_taken_branch_requires_the_store_and_exactly_one_update(self):
        item = next(row for row in self.report["observations"]
                    if row["leftBits"] == "7ff0000000000000" and row["rightBits"] == "fff0000000000000")
        self.assertEqual(item["resultBits"], "7ff0000000000000")
        self.assertEqual(item["updates"], 18)
        item["resultBits"] = item["rightBits"]
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()

    def test_missing_rows_and_wrong_validation_host_are_rejected(self):
        self.report["observations"].pop()
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()
        self.report["platform"] = "OSXPlayer"
        with self.assertRaisesRegex(ValueError, "wrong version, stage or platform"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
