import copy
import json
import tempfile
import unittest
from pathlib import Path

import scalar_double_accumulator as oracle


class ScalarDoubleAccumulatorOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": oracle.PROFILE, "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_separate_rounding_reassociation_and_even_odd_halfway_cases(self):
        expected = {17: 0, 18: 0x4010000000000000, 19: 0xbff0000000000000,
                    20: 0, 21: 0x3cc0000000000000, 22: 0x3ff0000000000000,
                    23: 0x3ff0000000000002, 24: 0x7ff0000000000000}
        for sample, result in expected.items():
            with self.subTest(sample=sample):
                self.assertEqual(oracle.applied_total(*oracle.SAMPLES[sample]), result)
        # A regrouped expression yields 1 here; the actual two rounded operations yield +0.
        self.assertEqual(oracle.add_bits(0x4340000000000000,
                                        oracle.add_bits(0xc340000000000000, 0xbff0000000000000,
                                                        subtract=True)), 0x3ff0000000000000)

    def test_signed_zero_subnormal_normal_and_invalid_boundaries(self):
        expected = {0: 0, 1: 0x8000000000000000, 2: 0, 3: 0, 8: 1,
                    9: 0x8000000000000001, 10: 0x0010000000000000,
                    11: 0x000fffffffffffff, 12: 1, 13: 0x8000000000000001,
                    14: 1, 15: 0x8000000000000001, 16: 1,
                    25: 0xfff8000000000000, 26: 0xfff8000000000000,
                    27: 0xfff0000000000000, 28: 0x7ff0000000000000}
        for sample, result in expected.items():
            with self.subTest(sample=sample):
                self.assertEqual(oracle.applied_total(*oracle.SAMPLES[sample]), result)
        self.assertEqual(oracle.rounded_units((1 << 53) + 1), 0x0020000000000000)
        self.assertEqual(oracle.rounded_units((1 << 53) + 3), 0x0020000000000002)

    def test_single_quiet_nan_preserves_payload_and_subtraction_sign(self):
        for sample, result in ((29, 0x7ff8000000000042), (30, 0xfff8000000000002),
                               (31, 0x7ff8123456789abc)):
            self.assertEqual(oracle.applied_total(*oracle.SAMPLES[sample]), result)
        self.assertEqual(oracle.add_bits(0x3ff0000000000000, 0xfff8000000000002, subtract=True),
                         0xfff8000000000002)

    def test_full_denominator_noop_bits_alias_repeat_and_classification_disclosure(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (2, 99))
        self.assertFalse(result["completeRawResultOracle"])
        self.assertEqual(result["classificationOnlyResults"],
                         [{"sample": sample, "kind": kind} for sample in (6, 7)
                          for kind in ("enabled", "repeat-alias")])
        for sample in range(32):
            disabled, enabled, repeated = self.report["observations"][2 + sample * 3:5 + sample * 3]
            self.assertEqual(disabled["state"]["totalBits"], disabled["totalInput"])
            self.assertEqual(enabled["state"], repeated["state"])
            self.assertTrue(repeated["alias"])
            self.assertFalse(enabled["state"]["pending"])

    def test_multiple_nan_classification_does_not_claim_exact_payload_or_accept_finite(self):
        changed = copy.deepcopy(self.report)
        row = next(row for row in changed["observations"]
                   if row.get("sample") == 6 and row["kind"] == "enabled")
        row["state"]["totalBits"] = "fff80000000000a7"
        with self.assertRaises(ValueError): self.verify(changed)
        repeat = next(row for row in changed["observations"]
                      if row.get("sample") == 6 and row["kind"] == "repeat-alias")
        repeat["state"]["totalBits"] = "fff80000000000a7"
        result = self.verify(changed)
        self.assertFalse(result["completeRawResultOracle"])
        for bits in ("7ff0000000000000", "7ff0000000000001", "7FF8000000000042", 0,
                     "0000000000000000"):
            row["state"]["totalBits"] = bits
            with self.subTest(bits=bits), self.assertRaises(ValueError): self.verify(changed)

    def test_exact_bit_state_order_type_and_exception_mutations_reject(self):
        mutations = ((1, "disabled", "totalBits", "0000000000000000"),
                     (6, "disabled", "currentBits", "7ff8000000000000"),
                     (17, "enabled", "totalBits", "3ff0000000000000"),
                     (8, "enabled", "totalBits", "0000000000000000"),
                     (29, "enabled", "totalBits", "7ff8000000000000"),
                     (30, "repeat-alias", "totalBits", "7ff8000000000002"),
                     (7, "enabled", "pending", True),
                     (7, "enabled", "baselineBits", "8000000000000000"))
        for sample, kind, field, replacement in mutations:
            with self.subTest(sample=sample, kind=kind, field=field):
                changed = copy.deepcopy(self.report)
                row = next(row for row in changed["observations"]
                           if row.get("sample") == sample and row["kind"] == kind)
                row["state"][field] = replacement
                with self.assertRaises(ValueError): self.verify(changed)
        changed = copy.deepcopy(self.report)
        changed["observations"][0]["totalType"] = "System.Single"
        with self.assertRaises(ValueError): self.verify(changed)
        changed = copy.deepcopy(self.report)
        changed["observations"][-1]["exception"] = "none"
        with self.assertRaises(ValueError): self.verify(changed)
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError): self.verify(dict(self.report, observations=rows))


if __name__ == "__main__":
    unittest.main()
