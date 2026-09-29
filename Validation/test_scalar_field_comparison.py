"""Protect the independent scalar-field value, effect and exception oracle."""

import json
from pathlib import Path
import tempfile
import unittest

import run_fixture
import scalar_field_comparison


class ScalarFieldComparisonBoundaryTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "scalar-field-comparison", "observations": list(scalar_field_comparison.observations())}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return scalar_field_comparison.verify(self.path, "player", run_fixture.VERSION)

    def test_quiet_nan_does_not_write_or_increment(self):
        item = next(row for row in self.report["observations"]
                    if row.get("leftBits") == "7fc00001" and row["rightBits"] == "3f800000")
        self.assertEqual(item["updates"], 17)
        self.assertEqual(self.verify()["resultChecks"], 678)
        item["updates"] = 18
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()

    def test_equal_signed_zero_preserves_field_bits(self):
        item = next(row for row in self.report["observations"]
                    if row.get("leftBits") == "00000000" and row["rightBits"] == "80000000")
        self.assertEqual(item["resultBits"], "80000000")
        item["resultBits"] = "00000000"
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()

    def test_taken_branch_must_store_and_increment_once(self):
        item = next(row for row in self.report["observations"]
                    if row.get("leftBits") == "7ff0000000000000" and row["rightBits"] == "fff0000000000000")
        self.assertEqual(item["updates"], 18)
        item["updates"] = 19
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()

    def test_null_receiver_must_fail_in_both_widths(self):
        self.assertEqual(self.verify()["observations"], 340)
        self.report["observations"][-1]["exception"] = "none"
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
