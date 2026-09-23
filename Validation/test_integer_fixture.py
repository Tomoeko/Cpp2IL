"""Protect the unsigned fixture's acceptance boundary; does not establish Unity execution."""

import json
from pathlib import Path
import tempfile
import unittest

import run_fixture


class IntegerFixtureBoundaryTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.path = self.root / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "integers", "observations": list(run_fixture.integer_observations())}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return run_fixture.verify_behavior(self.path, "player", "integers")

    def test_full_uint64_values_round_trip_without_float_coercion(self):
        receipt = self.verify()
        self.assertEqual(receipt["predicateChecks"], 200)
        values = json.loads(self.path.read_text())["observations"]
        self.assertEqual(values[-1]["left"], 18446744073709551615)
        self.report["observations"][-1]["left"] = float(18446744073709551615)
        with self.assertRaisesRegex(ValueError, "exact JSON integers"):
            self.verify()

    def test_signed_interpretation_is_rejected_at_boundary(self):
        item = next(item for item in self.report["observations"]
                    if item["width"] == 64 and item["left"] == 2**64 - 1 and item["right"] == 1)
        item["less"] = True
        with self.assertRaisesRegex(ValueError, "unsigned integer oracle"):
            self.verify()

    def test_partial_coverage_cannot_pass(self):
        self.report["observations"] = self.report["observations"][:25]
        with self.assertRaisesRegex(ValueError, "unsigned integer oracle"):
            self.verify()

    def test_numeric_booleans_cannot_pass(self):
        self.report["observations"][0]["less"] = 0
        with self.assertRaisesRegex(ValueError, "JSON booleans"):
            self.verify()

    def test_integer_harness_does_not_include_arithmetic_implementation(self):
        manifest = run_fixture.copy_harness("integers", self.root / "harness")
        self.assertEqual(len([item for item in manifest if item["path"].endswith("BehaviorProbe.cs")]), 1)
        reference = json.loads((self.root / "harness/Runtime/RecoveryValidation.Runtime.asmdef").read_text())
        self.assertEqual(reference["references"], ["IntegerFixture"])
        self.assertFalse(any(path.name == "RecoveryLogic.cs" for path in self.root.rglob("*.cs")))


if __name__ == "__main__":
    unittest.main()
