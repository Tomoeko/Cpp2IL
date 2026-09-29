import json
import tempfile
import unittest
from pathlib import Path

from ordered_generic_tail_field import TAIL_VALUES, VALUES, _guard, observations, verify


class OrderedGenericTailFieldOracleTests(unittest.TestCase):
    def check_report(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                  "profile": "ordered-generic-tail-field", "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_fixed_denominators_and_independent_full64_guard_arithmetic(self):
        rows = observations()
        self.assertEqual(len(rows), 1156)
        self.assertEqual(self.check_report(rows)["methods"], 2)
        self.assertEqual(sum(row["kind"] == "constructor" for row in rows), len(VALUES))
        self.assertEqual(sum(row["kind"] == "read" for row in rows), len(VALUES) * len(TAIL_VALUES) * 6)
        self.assertEqual(_guard(-(1 << 63)), -(1 << 31))
        self.assertEqual(_guard(-(1 << 63) + 1), -(1 << 31) + 1)
        self.assertEqual(_guard(-1), 0)
        self.assertEqual(_guard(1 << 32), 1)

    def test_constructor_value_and_tail_defaults_are_observed(self):
        for field, replacement in (("valueAfter", 0), ("tailItem", 1), ("tailGuard", 1)):
            rows = observations()
            rows[0][field] = replacement
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_tail_high_word_guard_or_neighbor_mutation_is_rejected(self):
        for field, replacement in (("tailItemAfter", 0), ("tailGuardAfter", 0),
                                    ("neighborAfter", 21), ("referenceSame", False)):
            rows = observations()
            rows[len(VALUES)][field] = replacement
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_repeated_alias_value_and_null_failure_are_observed(self):
        rows = observations()
        rows[len(VALUES)]["second"] = 0
        with self.assertRaises(ValueError):
            self.check_report(rows)
        rows = observations()
        rows[-1]["failure"] = "none"
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_numeric_boolean_and_null_identity_are_strict(self):
        for index, field, replacement in ((0, "tailItem", False), (len(VALUES), "first", float(-(1 << 31))),
                                         (len(VALUES), "aliasSame", 1), (-1, "value", 0)):
            rows = observations()
            rows[index][field] = replacement
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_missing_duplicate_rows_and_wrong_profile_are_rejected(self):
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check_report(rows)
        with self.assertRaises(ValueError):
            self.check_report(observations(), profile="guarded-scalar-accessor")


if __name__ == "__main__":
    unittest.main()
