import json
from pathlib import Path
import tempfile
import unittest

import sealed_parameter_class_test as oracle


class SealedParameterClassTestOracleTests(unittest.TestCase):
    def verify(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "behavior.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "stage": "player",
                                        "platform": "WindowsPlayer",
                                        "profile": "sealed-parameter-class-test",
                                        "observations": rows}), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_expected_cases_keep_two_methods_and_twelve_observations(self):
        self.assertEqual(self.verify(oracle.observations())["observations"], 12)
        self.assertEqual(self.verify(oracle.observations())["methods"], 2)

    def test_boolean_and_reference_results_are_independent(self):
        for key, value in (("testResult", 1), ("sameReference", False), ("nullResult", True)):
            with self.subTest(key=key):
                rows = oracle.observations()
                rows[8][key] = value
                with self.assertRaises(ValueError):
                    self.verify(rows)

    def test_selected_tests_cannot_add_formatting_or_builder_effects(self):
        for key, value in (("formatCalls", 1), ("builderText", "changed"),
                           ("failure", "System.InvalidCastException")):
            with self.subTest(key=key):
                rows = oracle.observations()
                rows[2][key] = value
                with self.assertRaises(ValueError):
                    self.verify(rows)


if __name__ == "__main__":
    unittest.main()
