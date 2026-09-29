"""Reject wrong component choice, aggregate mutation and unordered comparisons."""

import json
from pathlib import Path
import tempfile
import unittest

import aggregate_scalar_compare
import run_fixture


class AggregateScalarCompareBoundaryTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "aggregate-scalar-compare", "observations": list(aggregate_scalar_compare.observations())}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return aggregate_scalar_compare.verify(self.path, "player", run_fixture.VERSION)

    def test_both_components_and_caller_fields_are_covered(self):
        self.assertEqual(self.verify()["observations"], 1016)
        self.assertEqual(self.verify()["resultChecks"], 5072)
        for kind in ("static-low", "static-high", "instance-low", "instance-high",
                     "instance-low-compare", "instance-high-compare"):
            self.assertEqual(sum(row["kind"] == kind for row in self.report["observations"]), 169)

    def test_unordered_comparison_stays_false(self):
        row = next(row for row in self.report["observations"] if row["leftBits"] == "7fc00001")
        self.assertFalse(row["result"])
        row["result"] = True
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()

    def test_component_selection_cannot_use_the_other_field(self):
        row = next(row for row in self.report["observations"] if row["kind"] == "instance-high" and
                   row["leftBits"] == "3f800000" and row["rightBits"] == "bf800000")
        self.assertTrue(row["result"])
        row["result"] = False
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()

    def test_by_value_arguments_must_not_change_caller_storage(self):
        self.report["observations"][0]["secondHighAfter"] = "00000000"
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()

    def test_three_way_branches_cover_ordering_and_unordered_zero(self):
        for kind in ("instance-low-compare", "instance-high-compare"):
            rows = [row for row in self.report["observations"] if row["kind"] == kind]
            self.assertEqual({row["result"] for row in rows}, {-1, 0, 1})
            nan = next(row for row in rows if row["leftBits"] == "7fc00001")
            self.assertEqual(nan["result"], 0)
        nan["result"] = 1
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()

    def test_null_receiver_call_sites_preserve_failure(self):
        self.assertEqual(self.report["observations"][-1]["exception"], "System.NullReferenceException")
        self.report["observations"][-1]["exception"] = "none"
        with self.assertRaisesRegex(ValueError, "independent IEEE oracle"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
