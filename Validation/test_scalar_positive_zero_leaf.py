import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import scalar_positive_zero_leaf


class ScalarPositiveZeroLeafOracleTests(unittest.TestCase):
    def test_rejects_sign_null_and_declaration_mutations(self):
        report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer",
                  "stage": "player", "profile": "scalar-positive-zero-leaf",
                  "observations": scalar_positive_zero_leaf.observations()}
        with TemporaryDirectory() as temporary:
            path = Path(temporary) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            self.assertEqual(scalar_positive_zero_leaf.verify(
                path, "player", "2021.3.35f1")["observations"], 18)
            for index, key, wrong in ((0, "bits", "80000000"),
                                      (8, "exception", "none"),
                                      (10, "bits", "00000000"),
                                      (14, "static", True),
                                      (16, "count", True)):
                changed = json.loads(json.dumps(report))
                changed["observations"][index][key] = wrong
                path.write_text(json.dumps(changed), encoding="utf-8")
                with self.subTest(index=index, key=key):
                    with self.assertRaises(ValueError):
                        scalar_positive_zero_leaf.verify(path, "player", "2021.3.35f1")

            serialized = json.dumps(report)
            for malformed in (serialized.replace('"bits": "00000000"',
                                '"bits": "80000000", "bits": "00000000"', 1),
                              serialized.replace('"count": 1', '"count": NaN', 1)):
                path.write_text(malformed, encoding="utf-8")
                with self.assertRaises(ValueError):
                    scalar_positive_zero_leaf.verify(path, "player", "2021.3.35f1")
