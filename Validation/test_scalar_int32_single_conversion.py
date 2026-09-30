import json
from pathlib import Path
import tempfile
import unittest

from scalar_int32_single_conversion import observations, scaled_choice, single_bits, verify
from scalar_float_conversion_composition import divide


class SignedSingleConversionOracleTests(unittest.TestCase):
    def check(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                  "profile": "scalar-int32-single-conversion", "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_signed_rounding_ties_and_int32_limits(self):
        for value, bits in ((0, 0), (1, 0x3F800000), (-1, 0xBF800000),
                            (16777215, 0x4B7FFFFF), (16777216, 0x4B800000),
                            (16777217, 0x4B800000), (16777218, 0x4B800001),
                            (16777219, 0x4B800002), (-16777219, 0xCB800002),
                            (2147483647, 0x4F000000), (-2147483648, 0xCF000000)):
            with self.subTest(value=value):
                self.assertEqual(single_bits(value), bits)
        for invalid in (True, 1.0, 2147483648, -2147483649):
            with self.assertRaises(ValueError):
                single_bits(invalid)

    def test_ratio_rounds_both_integer_operands_before_division(self):
        self.assertEqual(divide(single_bits(16777219), single_bits(16777217)), 0x3F800002)
        self.assertEqual(divide(single_bits(0), single_bits(-1)), 0x80000000)
        self.assertEqual(divide(single_bits(1), single_bits(0)), 0x7F800000)
        self.assertEqual(divide(single_bits(-1), single_bits(0)), 0xFF800000)
        self.assertEqual(divide(single_bits(0), single_bits(0)), 0xFFC00000)

    def test_enum_decrement_wraps_before_conversion_and_exact_half_scaling(self):
        for value, bits in ((0, 0xBF000000), (1, 0), (2, 0x3F000000),
                            (-2147483648, 0x4E800000), (2147483647, 0x4E800000)):
            with self.subTest(value=value):
                self.assertEqual(scaled_choice(value), bits)

    def test_full_scope_preserves_marker_fields_and_shared_holder_reuse(self):
        rows = observations()
        result = self.check(rows)
        self.assertEqual(result["methods"], 7)
        self.assertEqual(result["observations"], 195)
        self.assertEqual(rows[0]["fields"], 11)
        self.assertEqual(rows[-4]["firstBits"], "4f000000")
        self.assertEqual(rows[-4]["secondBits"], "00000001")
        self.assertEqual(rows[-3]["firstBits"], "4f000000")
        self.assertEqual(rows[-3]["secondBits"], "cf000000")
        self.assertEqual(rows[-2]["resultBits"], "4b800002")
        self.assertTrue(all(row["sameOwner"] for row in rows[-4:]))
        self.assertTrue(all(row["count"] == 43 and row["divisor"] == -7 and row["choice"] == 29
                            for row in rows[-4:]))

    def test_unused_array_siblings_preserve_null_defaults_rank_contents_and_aliases(self):
        rows = observations()
        self.assertIsNone(rows[1]["samples"])
        self.assertIsNone(rows[1]["batches"])
        self.assertFalse(rows[1]["firstBatchIsSamples"])
        self.assertEqual(rows[0]["samplesRank"], 1)
        self.assertEqual(rows[0]["batchesNestedElement"], "System.Int32")
        for row in rows:
            if row["kind"] not in ("operation", "reuse"):
                continue
            self.assertEqual(row["samples"], [5, -17])
            self.assertEqual(row["batches"], [[5, -17], None, [5, -17]])
            self.assertTrue(all(row[name] for name in ("firstBatchIsSamples", "repeatedBatchAlias",
                                                     "sameSamples", "sameBatches", "sameFirstBatch")))
        for index, field, value in ((0, "samplesRank", 2), (0, "batchesType", "System.Int32[,]"),
                                    (0, "batchesNestedElement", "System.UInt32"),
                                    (1, "samples", []), (2, "samples", [5, -18]),
                                    (2, "batches", [[5, -17], None, [5, -18]]),
                                    (2, "firstBatchIsSamples", False), (-1, "sameBatches", False),
                                    (-1, "sameFirstBatch", False)):
            with self.subTest(index=index, field=field):
                changed = observations()
                changed[index][field] = value
                with self.assertRaises(ValueError):
                    self.check(changed)

    def test_typed_bits_member_identity_and_failure_mutations_reject(self):
        for index, field, value in ((2, "firstBits", "80000000"), (3, "firstBits", "00000000"),
                                    (4, "resultBits", "80000000"), (2, "count", True),
                                    (2, "input", 0.0), (-1, "sameOwner", False),
                                    (-5, "exception", "none"), (0, "enumUnderlying", "System.UInt32"),
                                    (0, "instanceConvertIsStatic", True), (0, "fields", 5)):
            with self.subTest(index=index, field=field):
                rows = observations()
                rows[index][field] = value
                with self.assertRaises(ValueError):
                    self.check(rows)
        for rows in (observations()[:-1], observations() + [observations()[-1]]):
            with self.assertRaises(ValueError):
                self.check(rows)
        with self.assertRaises(ValueError):
            self.check(observations(), profile="scalar-float-conversion")


if __name__ == "__main__":
    unittest.main()
