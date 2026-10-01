import json
from pathlib import Path
import struct
from tempfile import TemporaryDirectory
import unittest

import native_scalar_double_leaf as oracle


class NativeScalarDoubleLeafOracleTests(unittest.TestCase):
    def verify_rows(self, rows):
        with TemporaryDirectory() as folder:
            path = Path(folder) / 'observations.json'
            path.write_text(json.dumps({'unityVersion': '2021.3.35f1', 'platform': 'WindowsPlayer',
                                       'stage': 'player', 'profile': 'native-scalar-double-leaf',
                                       'observations': rows}))
            return oracle.verify(path, 'player', '2021.3.35f1')

    def test_signed_conversion_rounds_before_multiplication(self):
        # Independent host arithmetic cross-check for the finite default-rounding matrix.
        for value in oracle.LONG_SAMPLES:
            for _, coefficient in oracle.COEFFICIENTS:
                scale = struct.unpack('>d', coefficient.to_bytes(8, 'big'))[0]
                expected = int.from_bytes(struct.pack('>d', float(value) * scale), 'big')
                with self.subTest(value=value, coefficient=coefficient):
                    self.assertEqual(expected, oracle.scaled_long(value, coefficient))
        self.assertEqual(oracle.scaled_long(2**53, 0x3fc0000000000000),
                         oracle.scaled_long(2**53 + 1, 0x3fc0000000000000))
        self.assertNotEqual(oracle.scaled_long(2**53 + 1, 0x3fc0000000000000),
                            oracle.scaled_long(2**53 + 3, 0x3fc0000000000000))

    def test_signed_zero_nan_and_source_state_are_preserved(self):
        self.assertEqual(0x8000000000000000, oracle.add_bits(oracle.SIGN, oracle.SIGN))
        self.assertEqual(0, oracle.add_bits(oracle.SIGN, 0))
        for key in ('firstBits', 'secondBits', 'resultBits'):
            rows = oracle.observations()
            rows[2][key] = '0000000000000001'
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_rows(rows)

    def test_wrong_width_initializer_or_missing_scope_is_rejected(self):
        for row, key, value in ((0, 'conversionParameter', 'System.Int32'),
                                (0, 'typeInitializers', 0), (1, 'fieldsMarker', 0),
                                (-1, 'resultBits', '0000000000000000')):
            rows = oracle.observations()
            rows[row][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_rows(rows)
        with self.assertRaises(ValueError):
            self.verify_rows(oracle.observations()[:-1])

    def test_null_exception_and_alias_are_observed(self):
        for row, key, value in ((3, 'alias', False), (40, 'exception', 'none')):
            rows = oracle.observations()
            rows[row][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_rows(rows)
