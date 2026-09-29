import json
import tempfile
import unittest
from pathlib import Path

from wide_field_low32 import HIGH_WORDS, observations, verify


class WideFieldLow32OracleTests(unittest.TestCase):
    def check_report(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                  "profile": "wide-field-low32", "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_complete_high_words_preserve_only_low_word(self):
        rows = observations()
        for owner in ("signed", "unsigned"):
            for route, expected in (("signed", -1), ("unsigned", 0xffffffff)):
                selected = [row for row in rows if row["kind"] == "read" and row["owner"] == owner and
                            row["route"] == route and row["bitsBefore"] & 0xffffffff == 0xffffffff]
                self.assertEqual({row["bitsBefore"] >> 32 for row in selected}, set(HIGH_WORDS))
                self.assertEqual({row["result"] for row in selected}, {expected})

    def test_signed_32bit_boundary_is_independent_of_owner(self):
        rows = [row for row in observations() if row["kind"] == "read" and
                row["bitsBefore"] & 0xffffffff == 0x80000000]
        self.assertEqual({row["result"] for row in rows if row["route"] == "signed"}, {-0x80000000})
        self.assertEqual({row["result"] for row in rows if row["route"] == "unsigned"}, {0x80000000})

    def test_fixed_denominator_and_default_constructor_state(self):
        rows = observations()
        self.assertEqual(len(rows), 2310)
        self.assertEqual(rows[:2], [{"kind": "constructor", "owner": owner, "bits": 0,
                                    "neighbor": 0, "referenceNull": True} for owner in ("signed", "unsigned")])
        self.assertEqual(self.check_report(rows)["methods"], 6)

    def test_field_neighbor_and_reference_mutations_are_rejected(self):
        for field, value in (("bitsAfter", 1), ("neighborAfter", 0), ("referenceSame", False)):
            rows = observations()
            rows[2][field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_missing_null_or_wrong_exception_is_rejected(self):
        rows = observations()
        rows[-1]["failure"] = "none"
        with self.assertRaises(ValueError):
            self.check_report(rows)
        with self.assertRaises(ValueError):
            self.check_report(observations()[:-1])

    def test_numeric_and_reference_identity_types_are_strict(self):
        for field, value in (("bitsBefore", False), ("result", 0.0), ("referenceSame", 1)):
            rows = observations()
            rows[2][field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_duplicate_rows_and_target_mismatch_are_rejected(self):
        rows = observations()
        rows[3] = rows[2].copy()
        with self.assertRaises(ValueError):
            self.check_report(rows)
        for changes in ({"platform": "LinuxPlayer"}, {"profile": "integer-truncation"}, {"stage": "editor"}):
            with self.assertRaises(ValueError):
                self.check_report(observations(), **changes)

    def test_duplicate_json_keys_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text('{"observations": [], "observations": []}', encoding="utf-8")
            with self.assertRaises(ValueError):
                verify(path, "player", "2021.3.35f1")


if __name__ == "__main__":
    unittest.main()
