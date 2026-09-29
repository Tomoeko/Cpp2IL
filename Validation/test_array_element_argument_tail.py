import json
import tempfile
import unittest
from pathlib import Path

from array_element_argument_tail import observations, verify


class ArrayElementArgumentTailOracleTests(unittest.TestCase):
    def check_report(self, rows):
        report = {
            "unityVersion": "2021.3.35f1", "stage": "player",
            "platform": "WindowsPlayer", "profile": "array-element-argument-tail",
            "observations": rows,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_complete_cases(self):
        rows = observations()
        self.assertEqual(len(rows), 29)
        self.assertEqual(self.check_report(rows)["methods"], 5)
        self.assertEqual([row["step"] for row in rows if row["kind"] == "sequence"],
                         ["first", "second", "invalid", "null-value", "changed-value"])
        self.assertTrue(any(row["scenario"] == "element-null" and
                            row["failure"] == "System.NullReferenceException"
                            for row in rows if row["kind"] == "invoke"))

    def test_failure_order_and_null_argument(self):
        rows = observations()
        next(row for row in rows if row["kind"] == "invoke" and
             row["scenario"] == "array-null" and row["index"] == -1)["failure"] = (
                 "System.IndexOutOfRangeException")
        with self.assertRaises(ValueError):
            self.check_report(rows)

        rows = observations()
        next(row for row in rows if row["kind"] == "invoke" and
             row["scenario"] == "value-null" and row["index"] == 0)["callCount"] = 7
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_alias_and_argument_order(self):
        rows = observations()
        next(row for row in rows if row["kind"] == "invoke" and
             row["scenario"] == "element-alias" and row["index"] == 1)["last"] = "second"
        with self.assertRaises(ValueError):
            self.check_report(rows)

        rows = observations()
        next(row for row in rows if row.get("step") == "invalid")["callCount"] += 1
        with self.assertRaises(ValueError):
            self.check_report(rows)

        rows = observations()
        next(row for row in rows if row.get("step") == "changed-value")["last"] = "first"
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_typed_values_and_duplicate_keys(self):
        rows = observations()
        next(row for row in rows if row["kind"] == "invoke")["sameArray"] = 1
        with self.assertRaises(ValueError):
            self.check_report(rows)

        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text('{"unityVersion":"2021.3.35f1",'
                            '"unityVersion":"2021.3.35f1"}', encoding="utf-8")
            with self.assertRaises(ValueError):
                verify(path, "player", "2021.3.35f1")


if __name__ == "__main__":
    unittest.main()
