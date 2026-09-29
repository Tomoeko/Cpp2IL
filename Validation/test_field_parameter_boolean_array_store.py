import json
import tempfile
import unittest
from pathlib import Path

from field_parameter_boolean_array_store import observations, verify


class FieldParameterBooleanArrayStoreOracleTests(unittest.TestCase):
    def check(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps({
                "unityVersion": "2021.3.35f1", "stage": "player",
                "platform": "WindowsPlayer",
                "profile": "field-parameter-boolean-array-store",
                "observations": rows,
            }), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_cases_exercise_null_bounds_values_and_aliases(self):
        rows = observations()
        self.assertEqual(len(rows), 85)
        self.assertEqual(self.check(rows)["methods"], 2)
        self.assertEqual(rows[0]["valuesNull"], True)
        self.assertEqual(rows[0]["otherValuesNull"], True)
        self.assertEqual(rows[1]["failure"], "System.NullReferenceException")
        self.assertEqual(rows[29]["failure"], "System.IndexOutOfRangeException")
        self.assertTrue(any(row["failure"] == "none" and row["value"]
                            and row["after"] != row["before"] for row in rows[1:]))
        self.assertTrue(any(row["failure"] == "none" and not row["value"]
                            and row["after"] != row["before"] for row in rows[1:]))

    def test_oracle_rejects_effect_exception_identity_and_type_mutations(self):
        for row, key, changed in (
            (0, "prefix1", 1),
            (0, "otherValuesNull", False),
            (1, "failure", "none"),
            (43, "after", [True]),
            (71, "aliasSharesArray", False),
            (73, "otherAfter", [True, True, False]),
            (73, "neighbor", 0),
            (73, "value", 1),
        ):
            rows = observations()
            rows[row][key] = changed
            with self.assertRaises(ValueError):
                self.check(rows)
        with self.assertRaises(ValueError):
            self.check(observations()[:-1])


if __name__ == "__main__":
    unittest.main()
