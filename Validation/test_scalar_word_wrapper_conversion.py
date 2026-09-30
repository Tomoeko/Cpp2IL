import copy
import json
from pathlib import Path
import tempfile
import unittest

import scalar_word_wrapper_conversion as oracle


class ScalarWordWrapperConversionOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                       "profile": "scalar-word-wrapper-conversion", "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_exhaustive_complete_three_method_scope_is_lossless_and_bounded(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (3, 537))
        self.assertLess(len(json.dumps(self.report)), 1024 * 1024)
        rows = [row for row in self.report["observations"] if row["kind"] == "exhaustive"]
        for operation in ("wrap", "unwrap"):
            blocks = [row for row in rows if row["operation"] == operation]
            values = [int(row["resultBits"][index:index + 4], 16)
                      for row in blocks for index in range(0, 1024, 4)]
            self.assertEqual(values, list(range(65536)))
            self.assertEqual([row["startBits"] for row in blocks], list(range(0, 65536, 256)))

    def test_signed_results_copy_ref_alias_and_initializer_sentinel_are_separate(self):
        rows = {row.get("bits"): row for row in self.report["observations"] if row["kind"] == "boundary-alias"}
        for bits, signed in ((0x7fff, 32767), (0x8000, -32768), (0x8001, -32767), (0xffff, -1)):
            row = rows[bits]
            self.assertEqual(row["unwrappedValue"], signed)
            self.assertEqual(row["slotResult"], signed)
            self.assertEqual(row["copyValue"], 31)
            self.assertTrue(row["sameArray"])
            self.assertEqual((row["neighborValue"], row["seedValue"]), (-17, -17))
        self.assertTrue(self.report["observations"][0]["beforeFieldInit"])
        seeds = [row for row in self.report["observations"] if row["kind"].startswith("seed-")]
        self.assertEqual([row["bits"] for row in seeds], [65519, 65519])
        self.assertEqual([row["value"] for row in seeds], [-17, -17])

    def test_single_exhaustive_value_loss_and_block_permutation_reject(self):
        for operation in ("wrap", "unwrap"):
            for position in (0, 128, 255):
                with self.subTest(operation=operation, position=position):
                    changed = copy.deepcopy(self.report)
                    block = next(row for row in changed["observations"]
                                 if row["kind"] == "exhaustive" and row["operation"] == operation and row["startBits"] == 32768)
                    offset = position * 4
                    block["resultBits"] = block["resultBits"][:offset] + "0000" + block["resultBits"][offset + 4:]
                    with self.assertRaises(ValueError): self.verify(changed)
        changed = copy.deepcopy(self.report)
        changed["observations"][3], changed["observations"][5] = changed["observations"][5], changed["observations"][3]
        with self.assertRaises(ValueError): self.verify(changed)
        changed = copy.deepcopy(self.report)
        del changed["observations"][3]
        with self.assertRaises(ValueError): self.verify(changed)

    def test_typed_declarations_signed_width_alias_and_cctor_mutations_reject(self):
        changes = (
            ("declarations", None, "methods", 2),
            ("declarations", None, "valueFieldType", "System.UInt16"),
            ("declarations", None, "beforeFieldInit", False),
            ("declarations", None, "seedIsReadOnly", False),
            ("default", None, "after", 1),
            ("seed-before", None, "value", 65519),
            ("seed-after", None, "after", 0),
            ("boundary-alias", 0x8000, "unwrappedValue", 32768),
            ("boundary-alias", 0x8000, "slotResult", -32768.0),
            ("boundary-alias", 0xffff, "copyValue", -1),
            ("boundary-alias", 0xffff, "sameArray", 1),
            ("boundary-alias", 0xffff, "neighborValue", -1),
            ("exhaustive", None, "inputsUnchanged", False),
            ("exhaustive", None, "seedValue", 0),
        )
        for kind, bits, field, value in changes:
            with self.subTest(kind=kind, field=field):
                changed = copy.deepcopy(self.report)
                row = next(row for row in changed["observations"] if row["kind"] == kind and
                           (bits is None or row.get("bits") == bits))
                row[field] = value
                with self.assertRaises(ValueError): self.verify(changed)


if __name__ == "__main__":
    unittest.main()
