import json
import tempfile
import unittest
from pathlib import Path

from closed_generic_storage import INT_ITEMS, LONG_ITEMS, VALUES, _stamp, observations, verify


class ClosedGenericStorageOracleTests(unittest.TestCase):
    def check_report(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                  "profile": "closed-generic-storage", "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_denominators_and_independent_prefix_stamp(self):
        rows = observations()
        self.assertEqual(len(rows), 2062)
        self.assertEqual(self.check_report(rows)["methods"], 6)
        self.assertEqual(sum(row["kind"] == "constructor" for row in rows), len(VALUES) * 2)
        self.assertEqual(sum(row["kind"] == "state" for row in rows), len(VALUES) * (len(INT_ITEMS) + len(LONG_ITEMS)) * 6)
        self.assertEqual(_stamp(-(1 << 63)), -(1 << 31))
        self.assertEqual(_stamp(-1), 0)
        self.assertEqual(_stamp(1 << 32), 1)

    def test_constructor_defaults_and_value_are_checked(self):
        for field, replacement in (("valueAfter", 0), ("item", 1), ("stamp", 1), ("neighbor", 1), ("referenceNull", False)):
            rows = observations()
            rows[0][field] = replacement
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_reads_and_clear_state_are_checked(self):
        for field, replacement in (("first", 0), ("repeat", 0), ("clearedRead", 1), ("valueAfter", 1)):
            rows = observations()
            rows[len(VALUES) * 2][field] = replacement
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_prefix_neighbor_and_reference_mutation_are_rejected(self):
        for field, replacement in (("itemAfter", 0), ("stampAfter", 0), ("neighborAfter", 21),
                                    ("referenceSame", False), ("aliasSame", False)):
            rows = observations()
            rows[len(VALUES) * 2][field] = replacement
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_both_null_operations_and_numeric_types_are_strict(self):
        for index, field, replacement in ((-4, "failure", "none"), (-1, "failure", "none"),
                                         (0, "item", False), (42, "first", float(-(1 << 31))),
                                         (42, "aliasSame", 1), (-1, "value", 0)):
            rows = observations()
            rows[index][field] = replacement
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_missing_duplicate_or_wrong_profile_is_rejected(self):
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check_report(rows)
        with self.assertRaises(ValueError):
            self.check_report(observations(), profile="ordered-generic-tail-field")


if __name__ == "__main__":
    unittest.main()
