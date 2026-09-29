import json
import tempfile
import unittest
from pathlib import Path

from field_plus_one_reference_array import observations, verify


class FieldPlusOneReferenceArrayOracleTests(unittest.TestCase):
    def check_report(self, rows):
        report = {
            "unityVersion": "2021.3.35f1", "stage": "player",
            "platform": "WindowsPlayer",
            "profile": "field-plus-one-reference-array",
            "observations": rows,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_signed_plus_one_and_failure_order(self):
        rows = observations()
        self.assertEqual(len(rows), 70)
        self.assertEqual(self.check_report(rows)["methods"], 5)
        self.assertTrue(any(row["position"] == -1 and
                            row["exception"] == "none"
                            for row in rows if row["kind"] == "length-1"))
        self.assertTrue(any(row["position"] == (1 << 31) - 1 and
                            row["exception"] == "System.IndexOutOfRangeException"
                            for row in rows if row["kind"] == "length-3"))

    def test_null_and_bounds_are_not_interchangeable(self):
        rows = observations()
        next(row for row in rows if row["kind"] == "array-null" and
             row["position"] == (1 << 31) - 1)["exception"] = (
                 "System.IndexOutOfRangeException")
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_reference_identity_is_observed(self):
        rows = observations()
        next(row for row in rows if row["kind"] == "element-null")[
            "sameElement"] = False
        with self.assertRaises(ValueError):
            self.check_report(rows)


if __name__ == "__main__":
    unittest.main()
