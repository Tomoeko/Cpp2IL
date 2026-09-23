"""Protect NaN, signed-zero and coverage boundaries in the independent oracle."""

import json
from pathlib import Path
import tempfile
import unittest

import float_comparison
import run_fixture


class FloatComparisonBoundaryTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "float-comparisons", "observations": list(float_comparison.observations())}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return float_comparison.verify(self.path, "player", run_fixture.VERSION)

    def test_nan_is_unordered_even_against_itself(self):
        item = next(row for row in self.report["observations"]
                    if row["leftBits"] == row["rightBits"] == "7fc00001")
        self.assertEqual([item[key] for key in ("equal", "notEqual", "less", "lessOrEqual", "greater", "greaterOrEqual")],
                         [False, True, False, False, False, False])
        item["greaterOrEqual"] = not item["less"]
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()

    def test_signed_zero_identity_is_retained_but_compares_equal(self):
        item = next(row for row in self.report["observations"]
                    if row["leftBits"] == "00000000" and row["rightBits"] == "80000000")
        self.assertTrue(item["equal"])
        self.assertFalse(item["less"])
        self.assertEqual(self.verify()["resultChecks"], 2028)

    def test_partial_rows_and_numeric_booleans_are_rejected(self):
        self.report["observations"][0]["equal"] = 1
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()
        self.report["observations"] = []
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
