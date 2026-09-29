import json
import tempfile
import unittest
from pathlib import Path

from call_before_capture_boolean_store import COUNTERS, observations, verify


class CallBeforeCaptureBooleanStoreOracleTests(unittest.TestCase):
    def check_report(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player",
                  "platform": "WindowsPlayer",
                  "profile": "call-before-capture-boolean-store",
                  "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_complete_cases(self):
        rows = observations()
        self.assertEqual(len(rows), 2 + len(COUNTERS) * 4 * 2 + 1)
        self.assertEqual(self.check_report(rows)["methods"], 5)
        self.assertEqual(sum(row["kind"] == "invoke" and row["mode"] == 0
                             for row in rows), len(COUNTERS) * 2)
        self.assertTrue(any(row["kind"] == "invoke" and
                            row["counterBefore"] == (1 << 31) - 1 and
                            row["counterAfter"] == -(1 << 31) for row in rows))

    def test_call_effect_precedes_target_null_failure(self):
        rows = observations()
        row = next(row for row in rows if row["kind"] == "invoke" and row["mode"] == 0)
        row["counterAfter"] = row["counterBefore"]
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_receiver_identity_and_repeated_capture(self):
        for key, value in (("firstFlagAfter", True), ("secondFlagAfter", False),
                           ("otherSameFirst", True)):
            rows = observations()
            row = next(row for row in rows if row["kind"] == "invoke" and
                       row["mode"] == 2 and not row["flagBefore"])
            row[key] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)
        rows = observations()
        rows[-1]["secondFlag"] = False
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_strict_types_and_report(self):
        rows = observations()
        row = next(row for row in rows if row["kind"] == "invoke")
        row["firstFlagAfter"] = 1
        with self.assertRaises(ValueError):
            self.check_report(rows)
        with self.assertRaises(ValueError):
            self.check_report(observations()[:-1])
        for change in ({"stage": "editor"}, {"platform": "OSXPlayer"},
                       {"profile": "nested-literal-store"}):
            with self.assertRaises(ValueError):
                self.check_report(observations(), **change)

    def test_duplicate_json_key_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text('{"unityVersion":"2021.3.35f1",'
                            '"unityVersion":"2021.3.35f1"}', encoding="utf-8")
            with self.assertRaises(ValueError):
                verify(path, "player", "2021.3.35f1")


if __name__ == "__main__":
    unittest.main()
