"""Protect the nested getter's two distinct null-receiver observations."""

import json
from pathlib import Path
import tempfile
import unittest

import nested_boolean_getter
import run_fixture


class NestedBooleanGetterOracleTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files/validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name) / "behavior.json"
        self.report = {
            "unityVersion": run_fixture.VERSION,
            "stage": "player",
            "platform": "WindowsPlayer",
            "profile": "nested-boolean-getter",
            "observations": nested_boolean_getter.observations(),
        }

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return run_fixture.verify_behavior(self.path, "player", "nested-boolean-getter")

    def test_both_null_receivers_are_required(self):
        self.assertEqual(self.verify()["observations"], 21)
        for subject in ("null-child", "null-child-count", "null-owner", "null-owner-count"):
            with self.subTest(subject=subject):
                self.report["observations"] = nested_boolean_getter.observations()
                row = next(row for row in self.report["observations"]
                           if row["subject"] == subject and "exception" in row)
                row["exception"] = "none"
                with self.assertRaisesRegex(ValueError, "Boolean getter behavior"):
                    self.verify()

    def test_changed_child_and_neighbor_observations_are_required(self):
        self.assertEqual(self.verify()["observations"], 21)
        row = next(row for row in self.report["observations"]
                   if row["subject"] == "second-true" and row.get("check") == "result")
        row["result"] = False
        with self.assertRaisesRegex(ValueError, "Boolean getter behavior"):
            self.verify()
        self.report["observations"] = nested_boolean_getter.observations()
        self.report["observations"] = [
            row for row in self.report["observations"]
            if not (row["subject"] == "second-false" and row.get("check") == "neighbors")
        ]
        with self.assertRaisesRegex(ValueError, "Boolean getter behavior"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
