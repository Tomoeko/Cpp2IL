"""Protect Boolean class-test observations from result and effect substitutions."""

import json
from pathlib import Path
import tempfile
import unittest

import boolean_parameter_class_test
import run_fixture


class BooleanParameterClassTestOracleTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player",
                       "platform": "WindowsPlayer", "profile": "boolean-parameter-class-test",
                       "observations": boolean_parameter_class_test.observations()}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return boolean_parameter_class_test.verify(self.path, "player", run_fixture.VERSION)

    def test_null_and_subtype_results_cannot_be_substituted(self):
        self.assertEqual(self.verify()["observations"], 11)
        for kind in ("null-cold", "subtype"):
            row = next(item for item in self.report["observations"] if item["kind"] == kind)
            original = row["result"]
            row["result"] = not original
            with self.assertRaisesRegex(ValueError, "independent oracle"):
                self.verify()
            row["result"] = original

    def test_numeric_json_result_cannot_replace_boolean(self):
        row = next(item for item in self.report["observations"] if item["kind"] == "direct")
        row["result"] = 1
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_class_test_cannot_call_user_formatting_or_throw(self):
        row = next(item for item in self.report["observations"] if item["kind"] == "direct-repeat")
        row["formatCalls"] = 1
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()
        row["formatCalls"] = 0
        row["failure"] = "System.InvalidCastException"
        row["hresult"] = -2147467262
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
