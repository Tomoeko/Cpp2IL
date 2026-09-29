import json
import tempfile
import unittest
from pathlib import Path

from byref_integer_halves import ROUTES, observations, verify


class ByRefIntegerHalvesOracleTests(unittest.TestCase):
    def row(self, route, bits, alias=False, initial=0):
        return next(row for row in observations() if row["kind"] == "split" and row["route"] == route and
                    row["bits"] == bits and row["alias"] == alias and row["initial"] == initial)

    def check_report(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                  "profile": "byref-integer-halves", "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_fixed_method_and_observation_denominators(self):
        rows = observations()
        self.assertEqual(len(rows), 3460)
        self.assertEqual(self.check_report(rows)["methods"], 7)
        for route in ROUTES:
            self.assertEqual(sum(row["kind"] == "split" and row["route"] == route for row in rows), 576)

    def test_signed_and_unsigned_full_word_boundaries(self):
        bits = 0xffffffff80000000
        for route in ROUTES:
            row = self.row(route, bits)
            expected = (0x80000000, 0xffffffff) if route in ("static-unsigned", "instance-unsigned") else (-0x80000000, -1)
            self.assertEqual((row["slot0After"], row["slot1After"]), expected)

    def test_alias_observes_last_high_write_for_ref_and_out(self):
        bits = 0x1234567880000000
        for route in ROUTES:
            row = self.row(route, bits, alias=True, initial=2)
            self.assertEqual(row["slot0After"], 0x12345678)
            self.assertEqual(row["slot1After"], row["slot1Before"])

    def test_reordered_alias_store_or_guard_corruption_is_rejected(self):
        for field, replacement in (("slot0After", -0x80000000), ("guard0After", 0)):
            rows = observations()
            row = next(row for row in rows if row["kind"] == "split" and row["route"] == "instance-unsigned-to-signed" and
                       row["bits"] == 0x1234567880000000 and row["alias"] is True and row["initial"] == 2)
            row[field] = replacement
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_receiver_neighbor_and_reference_changes_are_rejected(self):
        for field, replacement in (("receiverNeighborAfter", 19), ("receiverReferenceSame", False)):
            rows = observations()
            row = next(row for row in rows if row["kind"] == "split" and row["route"] == "instance-signed")
            row[field] = replacement
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_null_receiver_precedes_all_stores(self):
        rows = observations()
        self.assertEqual([row["failure"] for row in rows[-3:]], ["System.NullReferenceException"] * 3)
        rows[-1]["slot0After"] = 0
        with self.assertRaises(ValueError):
            self.check_report(rows)
        rows = observations()
        rows[-1]["failure"] = "none"
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_numeric_and_boolean_identity_are_strict(self):
        for field, replacement in (("bits", False), ("initial", 0.0), ("alias", 0), ("storageSame", 1)):
            rows = observations()
            rows[1][field] = replacement
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_missing_duplicate_rows_or_wrong_target_are_rejected(self):
        for rows in (observations()[:-1], observations() + [observations()[1]]):
            with self.assertRaises(ValueError):
                self.check_report(rows)
        for changes in ({"stage": "editor"}, {"platform": "OSXPlayer"}, {"profile": "integer-truncation"}):
            with self.assertRaises(ValueError):
                self.check_report(observations(), **changes)


if __name__ == "__main__":
    unittest.main()
