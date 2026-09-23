"""Reject incomplete or weakened byte-field behavioral evidence."""

import json
from pathlib import Path
import tempfile
import unittest

import byte_fields
import run_fixture


class ByteFieldOracleTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files/validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "byte-fields", "observations": byte_fields.observations()}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return run_fixture.verify_behavior(self.path, "player", "byte-fields")

    def test_complete_values_pass_and_missing_boundary_fails(self):
        self.assertEqual(self.verify()["observations"], 519)
        self.report["observations"].pop(259)  # unsigned 255
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_field_mutation_is_not_a_read(self):
        self.report["observations"][260]["after"] = 0  # signed minimum
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_missing_conditional_store_fails(self):
        self.report["observations"][2]["observedAfter"] = False
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_swallowed_null_exception_fails(self):
        self.report["observations"][-1]["exception"] = "none"
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_numeric_boolean_is_not_accepted(self):
        self.report["observations"][4]["zero"] = 1
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
