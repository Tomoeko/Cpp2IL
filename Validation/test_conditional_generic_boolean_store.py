import json
import tempfile
import unittest
from pathlib import Path

from conditional_generic_boolean_store import COUNTERS, observations, verify


class ConditionalGenericBooleanStoreOracleTests(unittest.TestCase):
    def check_report(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player",
                  "platform": "WindowsPlayer",
                  "profile": "conditional-generic-boolean-store",
                  "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_denominator_and_generic_identity(self):
        rows = observations()
        self.assertEqual(len(rows), 81)
        self.assertEqual(self.check_report(rows)["methods"], 4)
        self.assertEqual(sum(row["kind"] == "flag" for row in rows), 32)
        self.assertEqual(sum(row["kind"] == "pair" for row in rows), 32)
        for owner in ("base", "string", "object"):
            self.assertEqual(sum(row["kind"] == "constructor" and
                                 row["owner"] == owner for row in rows), 1)

    def test_null_disabled_and_pair_values_are_distinct(self):
        for kind, field, value in (("null-flag", "returnedNull", False),
                                   ("null-pair", "failure", "System.NullReferenceException"),
                                   ("flag", "resultFlagAfter", False),
                                   ("pair", "counterAfter", COUNTERS[0])):
            rows = observations()
            row = next(row for row in rows if row["kind"] == kind and
                       (kind.startswith("null") or not row["enabled"]))
            row[field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_base_tag_neighbor_and_order_checks(self):
        for kind, field, value in (("flag", "returnedDerivedType", False),
                                   ("flag", "tagSame", False),
                                   ("pair", "identitySame", False),
                                   ("pair", "argumentAfter", 0),
                                   ("repeat", "counterAfterDisabled", 0),
                                   ("repeat", "resultAfterFlag", True),
                                   ("repeat", "neighborReferenceSame", False)):
            rows = observations()
            row = next(row for row in rows if row["kind"] == kind and
                       (kind == "repeat" or row["enabled"]))
            row[field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_strict_types_rows_and_target(self):
        for field, value in (("incomingCounter", False), ("counterAfter", float(COUNTERS[0]))):
            rows = observations()
            row = next(row for row in rows if row["kind"] == "pair")
            row[field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)
        with self.assertRaises(ValueError):
            self.check_report(observations()[:-1])
        for change in ({"stage": "editor"}, {"platform": "OSXPlayer"},
                       {"profile": "conditional-boolean-store"}):
            with self.assertRaises(ValueError):
                self.check_report(observations(), **change)


if __name__ == "__main__":
    unittest.main()
