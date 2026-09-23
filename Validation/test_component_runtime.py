"""Reject incomplete or misleading component runtime observations."""

import json
from pathlib import Path
import tempfile
import unittest

import run_fixture


class ComponentRuntimeTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "components", "observations": list(run_fixture.component_observations()),
                       "helpers": [{"input": value, "output": value} for value in (-(2**31), -1, 0, 1, 2**31 - 1)]}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return run_fixture.verify_behavior(self.path, "player", "components")

    def test_complete_scope_passes_but_missing_fields_do_not(self):
        self.assertEqual(self.verify()["observations"], 24)
        self.report["observations"].pop()
        with self.assertRaisesRegex(ValueError, "field observations"):
            self.verify()

    def test_field_identity_and_default_null_are_not_interchangeable(self):
        self.report["observations"][1]["value"] = ""
        with self.assertRaisesRegex(ValueError, "field observations"):
            self.verify()
        self.report["observations"][1]["value"] = None
        self.report["observations"][1]["managedType"] = "System.Object"
        with self.assertRaisesRegex(ValueError, "field observations"):
            self.verify()

    def test_reconstructed_reference_must_be_the_assigned_asset(self):
        self.report["observations"][8]["value"]["sameAsset"] = False
        with self.assertRaisesRegex(ValueError, "field observations"):
            self.verify()

    def test_boolean_and_default_return_cannot_replace_integer_results(self):
        self.report["observations"][0]["value"] = False
        with self.assertRaisesRegex(ValueError, "field observations"):
            self.verify()
        self.report["observations"][0]["value"] = 0
        self.report["helpers"][0]["output"] = 0
        with self.assertRaisesRegex(ValueError, "identity oracle"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
