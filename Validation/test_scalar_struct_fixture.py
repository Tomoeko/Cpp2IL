"""Check the scalar-struct oracle's overflow and coverage boundaries, without Unity."""

import json
from pathlib import Path
import tempfile
import unittest

import run_fixture


class ScalarStructFixtureBoundaries(unittest.TestCase):
    def setUp(self):
        root = run_fixture.ROOT / "Files" / "validation-tests"
        root.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=root)
        self.addCleanup(self.temporary.cleanup)
        self.report = Path(self.temporary.name) / "behavior.json"

    def verify(self, profile, observations):
        self.report.write_text(json.dumps({"unityVersion": run_fixture.VERSION, "stage": "player",
                                         "platform": "WindowsPlayer", "profile": profile,
                                         "observations": observations}), encoding="utf-8")
        return run_fixture.verify_behavior(self.report, "player", profile)

    def test_uint64_wrap_is_checked_as_an_exact_integer(self):
        observations = list(run_fixture.scalar_struct_observations("scalar-structs"))
        case = next(item for item in observations if item["width"] == 64 and item["left"] == 2**64 - 1 and item["right"] == 1)
        self.assertEqual(case["sum"], 0)
        self.assertEqual(self.verify("scalar-structs", observations)["observations"], 50)
        case["sum"] = 2**64
        with self.assertRaisesRegex(ValueError, "scalar struct oracle"):
            self.verify("scalar-structs", observations)

    def test_negative_layout_coverage_cannot_be_omitted(self):
        observations = list(run_fixture.scalar_struct_observations("scalar-structs-negative"))
        self.assertEqual(self.verify("scalar-structs-negative", observations)["observations"], 17)
        observations = [item for item in observations if item["case"] != "reference"]
        with self.assertRaisesRegex(ValueError, "scalar struct oracle"):
            self.verify("scalar-structs-negative", observations)

    def test_struct_harness_has_only_selected_api_reference(self):
        destination = self.report.parent / "harness"
        run_fixture.copy_harness("scalar-structs", destination)
        assembly = json.loads((destination / "Runtime/RecoveryValidation.Runtime.asmdef").read_text())
        self.assertEqual(assembly["references"], ["ScalarStructFixture"])
        self.assertFalse(any(path.name == "ScalarStructs.cs" for path in destination.rglob("*.cs")))


if __name__ == "__main__":
    unittest.main()
