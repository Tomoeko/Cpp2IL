import json
import tempfile
import unittest
from pathlib import Path

from conditional_generic_terminal_store import COUNTERS, METHODS, observations, verify


class ConditionalGenericTerminalStoreOracleTests(unittest.TestCase):
    def check_report(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player",
                  "platform": "WindowsPlayer",
                  "profile": "conditional-generic-terminal-store",
                  "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_denominator_and_call_variants(self):
        rows = observations()
        self.assertEqual(len(rows), 155)
        self.assertEqual(self.check_report(rows)["methods"], 6)
        for method in METHODS:
            self.assertEqual(sum(row["kind"] == "call" and
                                 row["method"] == method for row in rows), 32)

    def test_null_gate_and_order_sensitive_state(self):
        for kind, field, value in (("null", "returnedNull", False),
                                   ("call", "counterAfter", 0),
                                   ("call", "resultFlagAfter", False),
                                   ("repeat", "counterAfterDisabled", 0),
                                   ("repeat", "resultAfterFlag", True)):
            rows = observations()
            row = next(row for row in rows if row["kind"] == kind and
                       (kind != "call" or (row["method"] == "pair-early" and
                                           not row["enabled"])))
            row[field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_generic_identity_and_padding(self):
        for kind, field, value in (("constructor", "paddingZero", False),
                                   ("call", "returnedDerivedType", False),
                                   ("call", "identitySame", False),
                                   ("call", "tagSame", False),
                                   ("call", "paddingIntact", False),
                                   ("repeat", "neighborReferenceSame", False)):
            rows = observations()
            row = next(row for row in rows if row["kind"] == kind and
                       (kind != "constructor" or row["owner"] == "string"))
            row[field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_strict_numeric_types_count_and_target(self):
        for field, value in (("incomingCounter", False),
                             ("counterAfter", float(COUNTERS[0]))):
            rows = observations()
            row = next(row for row in rows if row["kind"] == "call")
            row[field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)
        with self.assertRaises(ValueError):
            self.check_report(observations()[:-1])
        for change in ({"stage": "editor"}, {"platform": "OSXPlayer"},
                       {"profile": "conditional-generic-boolean-store"}):
            with self.assertRaises(ValueError):
                self.check_report(observations(), **change)


if __name__ == "__main__":
    unittest.main()
