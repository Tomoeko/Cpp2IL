import json
import tempfile
import unittest
from pathlib import Path

from array_read_increment import observations, verify


class ArrayReadIncrementOracleTests(unittest.TestCase):
    def check(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player",
                  "platform": "WindowsPlayer", "profile": "array-read-increment",
                  "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_denominator_edges_and_wrap(self):
        rows = observations()
        self.assertEqual(len(rows), 21)
        self.assertEqual(self.check(rows)["methods"], 1)
        lookup = {(row["kind"], row["index"]): row for row in rows}
        self.assertEqual(lookup["single-max", 0]["result"], -(1 << 31))
        self.assertEqual(lookup["mixed", 3]["result"], -(1 << 31))
        self.assertEqual(lookup["mixed", 0]["result"], -6)
        self.assertEqual(lookup["mixed", 4]["exception"], "System.IndexOutOfRangeException")
        self.assertEqual(lookup["mixed", -1]["exception"], "System.IndexOutOfRangeException")
        self.assertEqual(lookup["null", -1]["exception"], "System.NullReferenceException")

    def test_wrong_result_exception_or_array_state_rejects(self):
        for kind, index, field, value in (
            ("single-max", 0, "result", (1 << 31) - 1),
            ("mixed", 4, "exception", "none"),
            ("null", -1, "exception", "System.IndexOutOfRangeException"),
            ("mixed", 0, "valuesAfter", [0, 0, 17, (1 << 31) - 1]),
        ):
            rows = observations()
            row = next(row for row in rows if row["kind"] == kind and row["index"] == index)
            row[field] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_denominator_and_report_identity_reject(self):
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check(rows)
        for field, value in (("unityVersion", "2022.3.0f1"),
                             ("stage", "editor"), ("profile", "array-access"),
                             ("platform", "OSXPlayer")):
            with self.assertRaises(ValueError):
                self.check(observations(), **{field: value})


if __name__ == "__main__":
    unittest.main()
