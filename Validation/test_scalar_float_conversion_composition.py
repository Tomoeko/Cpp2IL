import copy
import json
import tempfile
import unittest
from pathlib import Path

import scalar_float_conversion_composition as oracle


class ScalarFloatConversionCompositionOracleTests(unittest.TestCase):
    def verify(self, report, stage):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, stage, "2021.3.35f1")

    def report(self, stage):
        return {"unityVersion": "2021.3.35f1", "profile": "scalar-float-conversion-composition",
                "platform": "WindowsEditor" if stage == "editor" else "WindowsPlayer",
                "stage": stage, "observations": oracle.observations(stage)}

    def test_integer_ratio_rounding_and_exception_results(self):
        for left, right, result in (
                (0x3F800000, 0x40400000, 0x3EAAAAAB),
                (0xBF800000, 0x40400000, 0xBEAAAAAB),
                (0x00000001, 0x40000000, 0),
                (0x00000003, 0x40000000, 2),
                (0x00800000, 0x40000000, 0x00400000),
                (0x7F7FFFFF, 0x00000001, 0x7F800000),
                (0x80000000, 0x3F800000, 0x80000000),
                (0, 0, 0xFFC00000),
                (0x7F800000, 0x7F800000, 0xFFC00000),
                (0xFF800002, 0x7FC00001, 0xFFC00002)):
            with self.subTest(left=left, right=right):
                self.assertEqual(oracle.divide(left, right), result)

    def test_conversion_precedes_division_and_selection_preserves_zero_nan(self):
        rows = oracle.observations()
        self.assertEqual(rows[9 * 7]["resultBits"], "7f800000")
        self.assertEqual(rows[10 * 7]["resultBits"], "00000000")
        self.assertEqual(rows[11 * 7]["resultBits"], "3f800000")
        self.assertEqual(oracle.negative_to_positive(0x80000000), 0x80000000)
        self.assertEqual(oracle.negative_to_positive(0xFFC12345), 0xFFC12345)
        self.assertEqual(oracle.negative_to_positive(0xBF800000), 0x3F800000)

    def test_complete_stage_denominators_and_mutations(self):
        for stage in ("editor", "player"):
            report = self.report(stage)
            result = self.verify(report, stage)
            self.assertEqual((result["methods"], result["observations"]), (8, 229))
            for index, field, value in (
                    (9 * 7, "resultBits", "3f800000"),
                    (10 * 7, "resultBits", "3f000000"),
                    (8, "resultBits", "00000000"),
                    (27 * 7 + 1, "resultBits", "7fc00000"),
                    (31 * 7, "divisorArgumentBits", "ff800002" if stage == "editor" else "ffc00002"),
                    (224, "exception", "None"),
                    (228, "exception", "None")):
                with self.subTest(stage=stage, index=index, field=field):
                    changed = copy.deepcopy(report)
                    changed["observations"][index][field] = value
                    with self.assertRaises(ValueError): self.verify(changed, stage)
            with self.assertRaises(ValueError):
                self.verify(dict(report, observations=report["observations"][:-1]), stage)


if __name__ == "__main__":
    unittest.main()
