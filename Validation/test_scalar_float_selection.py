import copy
import json
from pathlib import Path
import tempfile
import unittest

import scalar_float_selection as oracle


class ScalarFloatSelectionTests(unittest.TestCase):
    def test_signed_zero_and_nan_payload_order(self):
        rows = { (row['width'], row['leftBits'], row['rightBits']): row
                 for row in oracle.observations() if row['kind'] == 'pair' }
        for left, right in [('00000000', '80000000'), ('80000000', '00000000'),
                            ('7f800001', '7fc00001'), ('3f800000', 'ff800002')]:
            row = rows[(32, left, right)]
            self.assertEqual(row['minimumBits'], right)
            self.assertEqual(row['maximumBits'], right)

    def test_rejects_changed_payload_or_zero_sign(self):
        rows = oracle.observations()
        for index in (1, 14):
            modified = copy.deepcopy(rows)
            modified[index]['maximumBits'] = '00000000'
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / 'report.json'
                path.write_text(json.dumps({'unityVersion': '2021.3.35f1', 'stage': 'player',
                    'platform': 'WindowsPlayer', 'profile': 'scalar-float-selection', 'observations': modified}))
                with self.assertRaises(ValueError): oracle.verify(path, 'player', '2021.3.35f1')

    def test_rejects_missing_or_changed_null_receiver_failures(self):
        rows = oracle.observations()
        for operation in ("read", "store"):
            for width in (32, 64):
                index = next(index for index, row in enumerate(rows)
                             if row['kind'] == 'null-receiver' and row['width'] == width
                             and row['operation'] == operation)
                for mutation in ("exception", "result", "missing"):
                    modified = copy.deepcopy(rows)
                    if mutation == "exception":
                        modified[index]['exception'] = 'none'
                    elif mutation == "result":
                        modified[index]['resultBits'] = '0' * (width // 4)
                    else:
                        modified.pop(index)
                    with self.subTest(width=width, operation=operation, mutation=mutation):
                        with tempfile.TemporaryDirectory() as directory:
                            path = Path(directory) / 'report.json'
                            path.write_text(json.dumps({'unityVersion': '2021.3.35f1', 'stage': 'player',
                                'platform': 'WindowsPlayer', 'profile': 'scalar-float-selection',
                                'observations': modified}))
                            with self.assertRaises(ValueError):
                                oracle.verify(path, 'player', '2021.3.35f1')
