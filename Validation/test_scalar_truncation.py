import copy
import json
from pathlib import Path
import unittest
from unittest.mock import Mock

from scalar_truncation import observations, truncated_signed_bits, verify


class ScalarTruncationOracleTests(unittest.TestCase):
    def report(self, stage="player"):
        return {"unityVersion": "2021.3.35f1", "stage": stage,
                "platform": "WindowsPlayer" if stage == "player" else "WindowsEditor",
                "profile": "scalar-truncation", "observations": observations()}

    def verify(self, report, stage="player"):
        return verify(Mock(spec=Path, read_text=Mock(return_value=json.dumps(report))), stage, "2021.3.35f1")

    def test_denominator_and_exact_signed_zero_subnormal_inputs(self):
        result = self.verify(self.report())
        self.assertEqual((result["observations"], result["resultChecks"], result["methods"]), (66, 132, 2))
        rows = observations()
        self.assertEqual(len({row["inputBits"] for row in rows}), 66)
        self.assertEqual([row["inputBits"] for row in rows[:4]],
                         ["0000000000000000", "8000000000000000", "0000000000000001", "8000000000000001"])
        for row in rows[:8]:
            self.assertEqual(row["int32Bits"], "00000000")
            self.assertEqual(row["int64Bits"], "0000000000000000")

    def test_truncation_toward_zero_is_not_rounding_or_floor(self):
        for input_bits, expected in (("3ff8000000000000", 1), ("bff8000000000000", -1),
                                     ("4004000000000000", 2), ("c004000000000000", -2),
                                     ("3fefffffffffffff", 0), ("bfefffffffffffff", 0)):
            for width in (32, 64):
                with self.subTest(input_bits=input_bits, width=width):
                    self.assertEqual(truncated_signed_bits(input_bits, width),
                                     format(expected % (1 << width), "0" + str(width // 4) + "x"))

    def test_int32_fractional_limits_are_checked_after_truncation(self):
        # 2^31 - one ULP still truncates to MAX, and -(2^31 + 0.5) to MIN.
        self.assertEqual(truncated_signed_bits("41dfffffffffffff", 32), "7fffffff")
        self.assertEqual(truncated_signed_bits("c1dfffffffe00000", 32), "80000001")
        self.assertEqual(truncated_signed_bits("c1e0000000100000", 32), "80000000")
        self.assertEqual(truncated_signed_bits("41e0000000000000", 32), "80000000")
        self.assertEqual(truncated_signed_bits("41e0000000100000", 64), "0000000080000000")
        self.assertEqual(truncated_signed_bits("c1e0000000200000", 64), "ffffffff7fffffff")

    def test_int64_limits_do_not_round_maximum_to_a_double(self):
        self.assertEqual(truncated_signed_bits("43dfffffffffffff", 64), "7ffffffffffffc00")
        self.assertEqual(truncated_signed_bits("c3dfffffffffffff", 64), "8000000000000400")
        for input_bits in ("43e0000000000000", "c3e0000000000000", "43e0000000000001", "c3e0000000000001"):
            with self.subTest(input_bits=input_bits):
                self.assertEqual(truncated_signed_bits(input_bits, 64), "8000000000000000")

    def test_nan_infinity_and_huge_finite_candidate_invalid_results(self):
        for input_bits in ("7ff8000000000001", "fffb123456789abc", "7ff0000000000000",
                           "fff0000000000000", "7fefffffffffffff", "ffefffffffffffff"):
            with self.subTest(input_bits=input_bits):
                self.assertEqual(truncated_signed_bits(input_bits, 32), "80000000")
                self.assertEqual(truncated_signed_bits(input_bits, 64), "8000000000000000")

    def test_wrong_result_normalized_input_missing_duplicate_numeric_results_reject(self):
        changed = self.report()
        changed["observations"][2]["int32Bits"] = "00000001"
        normalized = self.report()
        normalized["observations"][1]["inputBits"] = "0000000000000000"
        missing = self.report()
        missing["observations"].pop()
        duplicate = self.report()
        duplicate["observations"].append(copy.deepcopy(duplicate["observations"][0]))
        numeric = self.report()
        numeric["observations"][0]["int64Bits"] = 0
        saturated = self.report()
        next(row for row in saturated["observations"] if row["inputBits"] == "7ff0000000000000")["int32Bits"] = "7fffffff"
        for report in (changed, normalized, missing, duplicate, numeric, saturated):
            with self.subTest(report=report), self.assertRaisesRegex(ValueError, "bit-pattern oracle"):
                self.verify(report)

    def test_each_original_stage_has_its_own_unmodified_platform_check(self):
        self.assertEqual(self.verify(self.report("editor"), "editor")["observations"], 66)
        for key, value in (("unityVersion", "2021.3.34f1"), ("stage", "editor"),
                           ("profile", "integer-extensions"), ("platform", "OSXPlayer")):
            report = self.report()
            report[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify(report)
        report = self.report("editor")
        report["platform"] = "OSXEditor"
        with self.assertRaisesRegex(ValueError, "Windows"):
            self.verify(report, "editor")


if __name__ == "__main__":
    unittest.main()
