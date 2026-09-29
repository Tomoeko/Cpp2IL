import json
import tempfile
import unittest
from pathlib import Path

from enum_integer_conversion import observations, verify, word_bits


class EnumIntegerConversionOracleTests(unittest.TestCase):
    def check_report(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "stage": "player",
                                        "platform": "WindowsPlayer", "profile": "enum-integer-conversion",
                                        "observations": rows}), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_all_byte_inputs_and_word_boundaries_have_fixed_denominators(self):
        rows = observations()
        self.assertEqual(len(word_bits()), 38)
        self.assertEqual(len(rows), 1176)
        self.assertEqual(self.check_report(rows)["methods"], 8)
        self.assertEqual(len({r["bits"] for r in rows if r["route"] == "sbyte-i4"}), 256)
        self.assertEqual(len({r["bits"] for r in rows if r["route"] == "int16-i4"}), 38)

    def test_signed_negative_to_uint32_is_modulo32(self):
        rows = observations()
        for route, bits, expected in (("sbyte-u4", 128, (1 << 32) - 128),
                                      ("sbyte-u4", 255, (1 << 32) - 1),
                                      ("int16-u4", 32768, (1 << 32) - 32768),
                                      ("int16-u4", 65535, (1 << 32) - 1)):
            row = next(r for r in rows if r["route"] == route and r["bits"] == bits)
            self.assertEqual(row["result"], expected)
            changed = observations()
            changed[rows.index(row)]["result"] = bits
            with self.assertRaises(ValueError):
                self.check_report(changed)

    def test_undefined_enum_values_and_original_input_identity_are_observed(self):
        rows = observations()
        index = next(i for i, r in enumerate(rows) if r["route"] == "sbyte-i4" and r["bits"] == 2)
        for field, value in (("result", 0), ("inputAfter", 0), ("failure", "System.InvalidCastException")):
            changed = observations()
            changed[index][field] = value
            with self.assertRaises(ValueError):
                self.check_report(changed)

    def test_numeric_types_and_complete_route_denominator_are_strict(self):
        for field, value in (("result", False), ("bits", 0.0), ("inputAfter", False)):
            rows = observations()
            rows[0][field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check_report(rows)


if __name__ == "__main__":
    unittest.main()
