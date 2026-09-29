import json
import tempfile
import unittest
from pathlib import Path

from conditional_boolean_store import COUNTERS, VALUES, observations, verify


class ConditionalBooleanStoreOracleTests(unittest.TestCase):
    def check_report(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player",
                  "platform": "WindowsPlayer", "profile": "conditional-boolean-store",
                  "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_denominator_and_branch_boundaries(self):
        rows = observations()
        self.assertEqual(len(rows), 75)
        self.assertEqual(self.check_report(rows)["methods"], 3)
        self.assertEqual(sum(row["kind"] == "byte" for row in rows),
                         2 * len(VALUES) * len(COUNTERS))
        self.assertEqual(sum(row["kind"] == "pair" for row in rows),
                         2 * len(COUNTERS) * 2)
        self.assertTrue(any(row["kind"] == "pair" and row["enabled"] and
                            row["incoming"] == -(1 << 31) and
                            row["counterAfter"] == -(1 << 31) for row in rows))

    def test_skipped_store_and_null_result_are_required(self):
        for kind, key, value in (("byte", "valueAfter", 255),
                                 ("pair", "counterAfter", 0),
                                 ("null-byte", "returnedNull", False),
                                 ("null-pair", "failure", "System.NullReferenceException")):
            rows = observations()
            row = next(row for row in rows if row["kind"] == kind and
                       (kind.startswith("null") or not row["enabled"]))
            row[key] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_identity_neighbor_and_repeated_order_are_required(self):
        for kind, key, value in (("byte", "sameReference", False),
                                 ("pair", "neighborAfter", 0),
                                 ("repeat", "valueAfterDisabled", 0),
                                 ("repeat", "valueAfterEnabled", 17),
                                 ("repeat", "referenceSame", False)):
            rows = observations()
            row = next(row for row in rows if row["kind"] == kind and
                       (kind == "repeat" or row["enabled"]))
            row[key] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_strict_types_and_complete_report(self):
        for key, value in (("incoming", True), ("valueAfter", 17.0)):
            rows = observations()
            row = next(row for row in rows if row["kind"] == "byte")
            row[key] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)
        with self.assertRaises(ValueError):
            self.check_report(observations()[:-1])
        for change in ({"stage": "editor"}, {"platform": "OSXPlayer"},
                       {"profile": "byte-threshold"}):
            with self.assertRaises(ValueError):
                self.check_report(observations(), **change)


if __name__ == "__main__":
    unittest.main()
