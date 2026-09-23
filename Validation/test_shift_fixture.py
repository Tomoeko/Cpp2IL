"""Acceptance-boundary regressions for finite native shift observations."""

import json
from pathlib import Path
import tempfile
import unittest

import run_fixture


class ShiftFixtureBoundaryTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "shifts", "observations": list(run_fixture.shift_observations())}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return run_fixture.verify_behavior(self.path, "player", "shifts")

    def test_complete_boundary_vectors_preserve_uint64_precision(self):
        self.assertEqual(self.verify()["resultChecks"], 260)
        self.assertEqual(self.report["observations"][-1]["value"], 2**64 - 1)
        self.report["observations"][-1]["logical"] = float(2**63 - 1)
        with self.assertRaisesRegex(ValueError, "exact JSON integers"):
            self.verify()

    def test_logical_shift_cannot_be_replaced_by_arithmetic(self):
        item = next(item for item in self.report["observations"]
                    if item["width"] == 32 and item["value"] == 2**31 and item["count"] == 1)
        item["logical"] = item["arithmetic"]
        with self.assertRaisesRegex(ValueError, "independent shift oracle"):
            self.verify()

    def test_32_bit_count_mask_cannot_be_64_bit(self):
        item = next(item for item in self.report["observations"]
                    if item["width"] == 32 and item["value"] == 1 and item["count"] == 32)
        item["logical"] = 0
        with self.assertRaisesRegex(ValueError, "independent shift oracle"):
            self.verify()

    def test_partial_coverage_cannot_pass(self):
        self.report["observations"].pop()
        with self.assertRaisesRegex(ValueError, "independent shift oracle"):
            self.verify()

    def test_shift_harness_keeps_implementation_separate(self):
        target = self.path.parent / "harness"
        run_fixture.copy_harness("shifts", target)
        reference = json.loads((target / "Runtime/RecoveryValidation.Runtime.asmdef").read_text())
        self.assertEqual(reference["references"], ["ShiftFixture"])
        self.assertFalse(any(path.name == "IntegerShifts.cs" for path in target.rglob("*.cs")))


if __name__ == "__main__":
    unittest.main()
