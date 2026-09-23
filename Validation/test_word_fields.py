"""Guard against partial truth tables, hidden stores, and misleading field evidence."""

import base64
import copy
import json
from pathlib import Path
import tempfile
import unittest

import word_fields


class WordFieldOracleTests(unittest.TestCase):
    def setUp(self):
        scratch = Path(__file__).resolve().parent.parent / "Files/validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name) / "behavior.json"
        encode = lambda data: base64.b64encode(data).decode("ascii")
        zero = encode(b"\x01" + bytes(8191))
        unchanged = encode(b"\xff" * 8192)
        self.report = {
            "unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
            "profile": "word-fields", "patternCount": 65536,
            "bitEncoding": "unsigned16-pattern-index-lsb-first",
            "initialFields": [{"field": field, "managedType": kind, "value": 0}
                              for _, field, kind in word_fields.FIELD_CASES],
            "predicates": [{"member": member, "inputField": field, "inputType": kind,
                            "zeroResults": zero,
                            "unchangedFields": {entry[1]: unchanged for entry in word_fields.FIELD_CASES}}
                           for member, field, kind in word_fields.FIELD_CASES],
            "nullReceivers": [{"member": member, "exception": "System.NullReferenceException"}
                              for member, _, _ in word_fields.FIELD_CASES],
        }

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return word_fields.verify(self.path, "player", "2021.3.35f1")

    def test_complete_results_keep_predicate_and_mutation_denominators_separate(self):
        result = self.verify()
        self.assertEqual(result["predicateObservations"], 196608)
        self.assertEqual(result["fieldPreservationObservations"], 589824)
        self.assertEqual(result["observations"], 786438)

    def test_nonboundary_wrong_result_and_constant_fallback_fail(self):
        row = self.report["predicates"][0]
        bits = bytearray(base64.b64decode(row["zeroResults"]))
        bits[40000 >> 3] |= 1 << (40000 & 7)
        row["zeroResults"] = base64.b64encode(bits).decode("ascii")
        with self.assertRaisesRegex(ValueError, "results differs"):
            self.verify()
        row["zeroResults"] = base64.b64encode(bytes(8192)).decode("ascii")
        with self.assertRaisesRegex(ValueError, "results differs"):
            self.verify()

    def test_mutating_another_field_is_rejected(self):
        fields = self.report["predicates"][0]["unchangedFields"]
        bits = bytearray(base64.b64decode(fields["CharacterValue"]))
        bits[72 >> 3] &= ~(1 << (72 & 7))
        fields["CharacterValue"] = base64.b64encode(bits).decode("ascii")
        with self.assertRaisesRegex(ValueError, "preserves CharacterValue"):
            self.verify()

    def test_missing_duplicate_or_truncated_coverage_is_rejected(self):
        original = copy.deepcopy(self.report)
        self.report["predicates"].pop()
        with self.assertRaisesRegex(ValueError, "scope"):
            self.verify()
        self.report = copy.deepcopy(original)
        self.report["predicates"][1] = self.report["predicates"][0]
        with self.assertRaisesRegex(ValueError, "identity"):
            self.verify()
        self.report = copy.deepcopy(original)
        self.report["predicates"][0]["zeroResults"] = "AQ=="
        with self.assertRaisesRegex(ValueError, "exhaustive"):
            self.verify()

    def test_fresh_boolean_cannot_replace_an_integer_field_default(self):
        self.report["initialFields"][0]["value"] = False
        with self.assertRaisesRegex(ValueError, "Fresh word fields"):
            self.verify()

    def test_null_exception_and_platform_are_required(self):
        self.report["nullReceivers"][0]["exception"] = "none"
        with self.assertRaisesRegex(ValueError, "null calls"):
            self.verify()
        self.report["platform"] = "OSXPlayer"
        with self.assertRaisesRegex(ValueError, "Windows player"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
