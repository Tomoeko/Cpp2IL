"""Independent acceptance checks for guarded integer division observations."""

import json
from pathlib import Path
import tempfile
import unittest

import run_fixture


class DivisionFixtureBoundaryTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "division", "observations": list(run_fixture.division_observations())}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return run_fixture.verify_behavior(self.path, "player", "division")

    def test_signed_division_truncates_toward_zero(self):
        self.assertEqual(self.verify()["resultChecks"], 596)
        item = next(item for item in self.report["observations"]
                    if item["width"] == 32 and item["left"] == -17 and item["right"] == 2)
        self.assertEqual((item["quotient"], item["remainder"]), (-8, -1))
        item["quotient"], item["remainder"] = -9, 1
        with self.assertRaisesRegex(ValueError, "independent division oracle"):
            self.verify()

    def test_unsigned_high_bits_cannot_be_interpreted_as_signed(self):
        item = next(item for item in self.report["observations"]
                    if item["width"] == 64 and item["left"] == 2**64 - 1 and item["right"] == 2)
        self.assertEqual(item["quotient"], 2**63 - 1)
        item["quotient"] = 0
        with self.assertRaisesRegex(ValueError, "independent division oracle"):
            self.verify()

    def test_float_observations_cannot_hide_uint64_precision_loss(self):
        self.report["observations"][-1]["left"] = float(2**64 - 1)
        with self.assertRaisesRegex(ValueError, "exact JSON integers"):
            self.verify()

    def test_partial_or_empty_coverage_cannot_pass(self):
        self.report["observations"].pop()
        with self.assertRaisesRegex(ValueError, "independent division oracle"):
            self.verify()
        self.report["observations"] = []
        with self.assertRaisesRegex(ValueError, "independent division oracle"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
