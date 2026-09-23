"""Acceptance checks for the positive metadata guard's repeated string materialization."""

import json
from pathlib import Path
import tempfile
import unittest

import run_fixture


class MetadataLiteralBoundaryTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "metadata-literal", "observations": [
                           {"repeat": repeat, "literal": "neutral metadata literal", "sameInstance": True}
                           for repeat in range(2)]}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return run_fixture.verify_behavior(self.path, "player", "metadata-literal")

    def test_complete_evidence_passes_but_missing_repeat_does_not(self):
        self.assertEqual(self.verify()["observations"], 2)
        self.report["observations"].pop()
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_different_literal_is_rejected(self):
        self.report["observations"][0]["literal"] = "replacement"
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_reallocating_equal_strings_is_rejected(self):
        self.report["observations"][1]["sameInstance"] = False
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
