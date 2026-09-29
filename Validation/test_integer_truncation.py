import unittest
import json
import tempfile
from pathlib import Path

from integer_truncation import expected_observations, verify


class IntegerTruncationOracleTests(unittest.TestCase):
    def row(self, operation, bits, initial=0):
        return next(row for row in expected_observations() if row["operation"] == operation and
                    row["bits"] == bits and row["initial"] == initial)

    def test_high_bits_do_not_change_low_half(self):
        self.assertEqual(self.row("lowUnsigned", 0x80000000ffffffff)["result"], 0xffffffff)
        self.assertEqual(self.row("lowSigned", 0xffffffffffffffff)["result"], -1)

    def test_high_half_preserves_signed_boundary(self):
        self.assertEqual(self.row("highSigned", 0x8000000000000000)["result"], -0x80000000)
        self.assertEqual(self.row("highUnsigned", 0x8000000000000000)["result"], 0x80000000)

    def test_split_writes_only_its_declared_fields(self):
        signed = self.row("splitSigned", 0x123456789abcdef0, 2)
        self.assertEqual((signed["signedLow"], signed["signedHigh"]), (-1698898192, 305419896))
        self.assertEqual((signed["unsignedLow"], signed["unsignedHigh"]), (0xffffffff, 0xffffffff))
        unsigned = self.row("splitUnsigned", 0xffffffffffffffff, 1)
        self.assertEqual((unsigned["unsignedLow"], unsigned["unsignedHigh"]), (0xffffffff, 0xffffffff))
        self.assertEqual((unsigned["signedLow"], unsigned["signedHigh"]), (-0x80000000, -0x80000000))

    def test_fixed_denominator(self):
        self.assertEqual(len(expected_observations()), 120)

    def test_numeric_identity_rejects_boolean_and_float_substitutes(self):
        for replacement in (False, 0.0):
            rows = expected_observations()
            rows[0]["bits"] = replacement
            report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                      "profile": "integer-truncation", "observations": rows}
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "report.json"
                path.write_text(json.dumps(report), encoding="utf-8")
                with self.assertRaises(ValueError):
                    verify(path, "player", "2021.3.35f1")


if __name__ == "__main__":
    unittest.main()
