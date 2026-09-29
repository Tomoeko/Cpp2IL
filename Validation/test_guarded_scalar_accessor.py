import json
import tempfile
import unittest
from pathlib import Path

from guarded_scalar_accessor import CODES, observations, verify


class GuardedScalarAccessorOracleTests(unittest.TestCase):
    def check_report(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                  "profile": "guarded-scalar-accessor", "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_fixed_denominators_and_defined_undefined_enum_boundaries(self):
        rows = observations()
        self.assertEqual(len(rows), 259)
        self.assertEqual(self.check_report(rows)["methods"], 7)
        self.assertEqual(len(CODES), 21)
        self.assertEqual(CODES[0], -(1 << 31))
        self.assertEqual(CODES[-1], (1 << 31) - 1)
        self.assertEqual(sum(row["kind"] == "read" for row in rows), 252)

    def test_second_alias_read_and_scalar_signedness_are_observed(self):
        rows = observations()
        row = next(row for row in rows if row["kind"] == "read" and row["code"] == -(1 << 31))
        self.assertEqual(row["codeFirst"], -(1 << 31))
        row["codeSecond"] = 1 << 31
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_mutating_backing_fields_neighbors_or_alias_identity_is_rejected(self):
        for field, value in (("flagAfter", True), ("codeAfter", 17), ("neighborAfter", 21),
                             ("referenceSame", False), ("stateSame", False), ("aliasStateSame", False),
                             ("arraySame", False)):
            rows = observations()
            rows[3][field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_array_replacement_mutation_shape_and_numeric_types_are_rejected(self):
        for value in (None, [], [0, -(1 << 31), -(1 << 31), (1 << 31) - 1],
                      [-137, float(-(1 << 31)), -(1 << 31), (1 << 31) - 1],
                      [-137, -(1 << 31), False, (1 << 31) - 1]):
            rows = observations()
            rows[3]["arrayAfter"] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_null_state_and_receiver_failures_are_distinct_rows(self):
        rows = observations()
        self.assertEqual([row["route"] for row in rows[-4:]],
                         ["state-flag", "state-code", "receiver-flag", "receiver-code"])
        for offset in range(1, 5):
            rows = observations()
            rows[-offset]["valueFailure"] = "none"
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_boolean_integer_and_null_identity_are_strict(self):
        for index, field, value in ((3, "flagFirst", 0), (3, "codeFirst", float(-(1 << 31))),
                                    (3, "stateSame", 1), (3, "arraySame", 1), (0, "arrayNull", 1),
                                    (-1, "value", False)):
            rows = observations()
            rows[index][field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_missing_duplicate_constructor_or_wrong_target_is_rejected(self):
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check_report(rows)
        for changes in ({"stage": "editor"}, {"platform": "OSXPlayer"}, {"profile": "byref-integer-halves"}):
            with self.assertRaises(ValueError):
                self.check_report(observations(), **changes)


if __name__ == "__main__":
    unittest.main()
