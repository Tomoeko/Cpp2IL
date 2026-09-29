import json
import tempfile
import unittest
from pathlib import Path

from small_aggregate_getter import observations, verify


class SmallAggregateGetterOracleTests(unittest.TestCase):
    def row(self, operation, bits):
        return next(row for row in observations() if row["operation"] == operation and row["bits"] == bits)

    def check_report(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                  "profile": "small-aggregate-getter", "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_all_byte_bits_and_fixed_denominator(self):
        rows = observations()
        for operation in ("readSByte", "widenSByte", "readByte", "widenByte"):
            self.assertEqual([row["bits"] for row in rows if row["operation"] == operation], list(range(256)))
        self.assertEqual(len(rows), 1108)
        self.assertEqual(self.check_report(rows)["methods"], 8)

    def test_signed_and_unsigned_byte_results_remain_distinct(self):
        self.assertEqual(self.row("readSByte", 0x80)["result"], -128)
        self.assertEqual(self.row("widenSByte", 0xff)["result"], -1)
        self.assertEqual(self.row("readByte", 0x80)["result"], 128)
        self.assertEqual(self.row("widenByte", 0xff)["result"], 255)

    def test_signed_and_unsigned_16bit_boundaries(self):
        self.assertEqual(self.row("readInt16", 0x8000)["result"], -32768)
        self.assertEqual(self.row("widenInt16", 0xffff)["result"], -1)
        self.assertEqual(self.row("readUInt16", 0x8000)["result"], 32768)
        self.assertEqual(self.row("widenUInt16", 0xffff)["result"], 65535)

    def test_wrong_signedness_or_changed_input_is_rejected(self):
        for field, value in (("result", 128), ("after", 0)):
            rows = observations()
            row = next(row for row in rows if row["operation"] == "widenSByte" and row["bits"] == 0x80)
            row[field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_numeric_types_and_denominator_are_strict(self):
        for replacement in (False, 0.0, None):
            rows = observations()
            rows[0]["result"] = replacement
            with self.assertRaises(ValueError):
                self.check_report(rows)
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_target_header_and_unexpected_fields_are_rejected(self):
        for changes in ({"platform": "OSXEditor"}, {"unityVersion": "2021.3.34f1"}, {"stage": "editor"}):
            with self.assertRaises(ValueError):
                self.check_report(observations(), **changes)
        rows = observations()
        rows[0]["invented"] = 0
        with self.assertRaises(ValueError):
            self.check_report(rows)


if __name__ == "__main__":
    unittest.main()
