import json
import tempfile
import unittest
from pathlib import Path

from fixed_boolean_conjunction import observations, verify


class FixedBooleanConjunctionOracleTests(unittest.TestCase):
    def check(self, rows, **changes):
        report = {
            "unityVersion": "2021.3.35f1", "stage": "player",
            "platform": "WindowsPlayer", "profile": "fixed-boolean-conjunction",
            "observations": rows, **changes,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_short_circuit_and_ordered_exceptions(self):
        rows = observations()
        self.assertEqual(len(rows), 35)
        self.assertEqual(self.check(rows)["methods"], 4)
        self.assertEqual(rows[5]["result"], False)
        self.assertEqual(rows[6]["result"], False)
        self.assertEqual(rows[7]["exception"], "System.NullReferenceException")
        self.assertEqual(rows[10]["exception"], "System.IndexOutOfRangeException")
        self.assertEqual(rows[11]["exception"], "System.NullReferenceException")
        self.assertEqual(rows[12]["exception"], "System.IndexOutOfRangeException")
        self.assertEqual(rows[16]["result"], True)
        self.assertEqual(rows[16]["firstBefore"], [True, False])
        self.assertEqual(rows[31]["exception"], "System.IndexOutOfRangeException")
        self.assertEqual(rows[32]["exception"], "System.IndexOutOfRangeException")
        self.assertEqual(rows[33]["result"], False)
        self.assertEqual(rows[34]["firstSecondSame"], True)

    def test_effect_and_failure_mutations_reject(self):
        for index, key, value in (
                (5, "exception", "System.NullReferenceException"),
                (10, "exception", "System.NullReferenceException"),
                (11, "result", True),
                (1, "result", 1),
                (1, "firstBefore", [0]),
                (13, "firstSecondSame", False),
                (14, "secondAfter", [False]),
                (1, "firstFieldSame", False),
                (1, "firstFieldSame", 1),
                (2, "secondFieldSame", False),
                (3, "sentinelAfter", 74),
                (16, "result", False),
                (16, "method", "BothFalseAtZero"),
                (31, "exception", "System.NullReferenceException"),
                (33, "exception", "System.IndexOutOfRangeException"),
                (0, "atOneReturn", "System.Int32"),
                (0, "baseType", "FixedBooleanConjunctionFixture.EmptyBase"),
                (0, "baseArgument", "System.String"),
                (0, "baseFields", 1),
        ):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_denominator_and_target_mutations_reject(self):
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check(rows)
        for field, value in (
                ("unityVersion", "2022.3.0f1"), ("stage", "editor"),
                ("platform", "OSXPlayer"), ("profile", "field-boolean-array-read"),
        ):
            with self.assertRaises(ValueError):
                self.check(observations(), **{field: value})


if __name__ == "__main__":
    unittest.main()
