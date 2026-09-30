import copy
import json
import tempfile
import unittest
from pathlib import Path

import native_subnormal_field_store as oracle


class NativeSubnormalFieldStoreOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": "native-subnormal-field-store", "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_exact_denominator_extreme_bits_and_two_store_composition(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (11, 66))
        rows = {(row.get("operation"), row["kind"]): row for row in self.report["observations"]}
        self.assertEqual(rows[0, "success"]["holder"]["singleBits"], 1)
        self.assertEqual(rows[1, "success"]["holder"]["singleBits"], 0x007fffff)
        self.assertEqual(rows[2, "success"]["holder"]["singleBits"], -2147483647)
        self.assertEqual(rows[3, "success"]["holder"]["singleBits"], -2139095041)
        self.assertEqual(rows[4, "success"]["holder"]["doubleBits"], 1)
        self.assertEqual(rows[5, "success"]["holder"]["doubleBits"], 0x000fffffffffffff)
        self.assertEqual(rows[5, "success"]["holder"]["singleBits"], -2147483647)
        self.assertEqual(rows[6, "success"]["holder"]["doubleBits"], -9223372036854775807)
        self.assertEqual(rows[7, "success"]["holder"]["doubleBits"], -9218868437227405313)
        self.assertEqual(rows[5, "target-null"]["holder"]["singleBits"], 0x3f800000)
        self.assertEqual(rows[5, "target-null"]["holder"]["doubleBits"], 0x3ff0000000000000)
        self.assertEqual(rows[5, "repeat"]["first"], {"calls": 9, "value": True})
        self.assertEqual(rows[2, "alias"]["first"], rows[2, "alias"]["second"])

    def test_subnormal_sign_width_neighbor_and_partial_two_store_mutations_reject(self):
        mutations = (
            (0, "success", "holder", "singleBits", 0),
            (0, "success", "holder", "singleBits", True),
            (1, "success", "holder", "singleBits", 0x00800000),
            (2, "success", "holder", "singleBits", 1),
            (3, "success", "holder", "singleBits", 0x807fffff),
            (4, "success", "holder", "doubleBits", 0),
            (4, "success", "holder", "singleBits", 1),
            (5, "success", "holder", "doubleBits", 0x3ff0000000000000),
            (5, "success", "holder", "singleBits", 0x3f800000),
            (6, "success", "holder", "doubleBits", -2147483647),
            (7, "success", "holder", "doubleBits", 0x800fffffffffffff),
            (5, "target-null", "holder", "doubleBits", 0x000fffffffffffff),
            (5, "target-null", "first", "calls", 8),
            (2, "alias", "second", "value", True),
            (6, "false-success", "first", "value", 0),
            (7, "repeat", "first", "calls", 8),
        )
        for operation, kind, state, field, replacement in mutations:
            with self.subTest(operation=operation, kind=kind, field=field):
                changed = copy.deepcopy(self.report)
                row = next(row for row in changed["observations"]
                           if row["kind"] == kind and row.get("operation") == operation)
                row[state][field] = replacement
                with self.assertRaises(ValueError): self.verify(changed)

    def test_failure_channel_coverage_and_order_mutations_reject(self):
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError): self.verify(dict(self.report, observations=rows))
        changed = copy.deepcopy(self.report)
        row = next(row for row in changed["observations"] if row["kind"] == "holder-null")
        row["exception"] = "none"
        with self.assertRaises(ValueError): self.verify(changed)


if __name__ == "__main__":
    unittest.main()
