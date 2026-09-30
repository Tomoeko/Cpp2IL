import copy
import json
from pathlib import Path
import tempfile
import unittest

import scalar_float_conversion as oracle


class ScalarFloatConversionTests(unittest.TestCase):
    def test_narrowing_rounds_boundaries_and_nan_payloads(self):
        cases = {0x3FF0000010000000: 0x3F800000, 0x3FF0000010000001: 0x3F800001,
                 0x3FF0000030000000: 0x3F800002, 0x47EFFFFFF0000000: 0x7F800000,
                 0x47EFFFFFEFFFFFFF: 0x7F7FFFFF, 0x3690000000000000: 0,
                 0x3690000000000001: 1, 0x380FFFFFF0000000: 0x00800000,
                 0xFFF0000000000002: 0xFFC00000, 0x8000000000000000: 0x80000000}
        for source, result in cases.items():
            with self.subTest(source=source):
                self.assertEqual(oracle.convert(source, 64), result)

    def test_widening_retains_value_zero_sign_and_quiets_nan(self):
        for source, result in ((1, 0x36A0000000000000), (0x80000000, 0x8000000000000000),
                               (0x7F800001, 0x7FF8000020000000), (0xFFC00002, 0xFFF8000040000000)):
            with self.subTest(source=source):
                self.assertEqual(oracle.convert(source, 32), result)

    def test_changed_bits_argument_quieting_and_coverage_reject(self):
        for stage in ("editor", "player"):
            rows = oracle.observations(stage)
            for mutation in ("zero-sign", "nan-payload", "argument", "rounding", "missing"):
                changed = copy.deepcopy(rows)
                if mutation == "missing": changed.pop()
                else:
                    width, bits = (32, "80000000") if mutation == "zero-sign" else (
                        (32, "7f800001") if mutation in ("nan-payload", "argument") else (64, "3ff0000010000001"))
                    row = next(row for row in changed if row['sourceWidth'] == width and row['inputBits'] == bits)
                    key = "argumentBits" if mutation == "argument" else "resultBits"
                    row[key] = "0" * len(row[key])
                with self.subTest(stage=stage, mutation=mutation), tempfile.TemporaryDirectory() as directory:
                    path = Path(directory) / 'report.json'
                    path.write_text(json.dumps({'unityVersion': '2021.3.35f1', 'stage': stage,
                        'platform': 'WindowsEditor' if stage == 'editor' else 'WindowsPlayer',
                        'profile': 'scalar-float-conversion', 'observations': changed}))
                    with self.assertRaises(ValueError): oracle.verify(path, stage, '2021.3.35f1')
