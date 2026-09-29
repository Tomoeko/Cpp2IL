import json
import tempfile
import unittest
from pathlib import Path

from call_result_string_tail import observations, verify


class CallResultStringTailOracleTests(unittest.TestCase):
    def check_report(self, rows):
        report = {
            "unityVersion": "2021.3.35f1", "stage": "player",
            "platform": "WindowsPlayer", "profile": "call-result-string-tail",
            "observations": rows,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_complete_cases(self):
        rows = observations()
        self.assertEqual(len(rows), 12)
        self.assertEqual(self.check_report(rows)["methods"], 5)
        self.assertEqual([row["scenario"] for row in rows
                          if row["kind"] == "sequence"],
                         ["first-host", "second-host", "changed-value",
                          "missing-node", "restored", "null-string"])

    def test_failure_and_effect_order(self):
        rows = observations()
        next(row for row in rows if row.get("scenario") == "missing-node"
             and row["kind"] == "single")["failure"] = "none"
        with self.assertRaises(ValueError):
            self.check_report(rows)

        rows = observations()
        next(row for row in rows if row.get("scenario") == "missing-node"
             and row["kind"] == "single")["textCalls"] = 5
        with self.assertRaises(ValueError):
            self.check_report(rows)

        rows = observations()
        next(row for row in rows if row.get("scenario") == "host-null")[
            "nodeSameWitness"] = False
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_alias_and_null_string(self):
        rows = observations()
        next(row for row in rows if row.get("scenario") == "second-host")[
            "textCalls"] = 8
        with self.assertRaises(ValueError):
            self.check_report(rows)

        rows = observations()
        next(row for row in rows if row.get("scenario") == "missing-node"
             and row["kind"] == "sequence")["hostsShareNode"] = True
        with self.assertRaises(ValueError):
            self.check_report(rows)

        rows = observations()
        next(row for row in rows if row.get("scenario") == "null-string")[
            "result"] = ""
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_typed_values_and_duplicate_keys(self):
        rows = observations()
        next(row for row in rows if row.get("scenario") == "value")[
            "currentSameNode"] = 1
        with self.assertRaises(ValueError):
            self.check_report(rows)

        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text('{"unityVersion":"2021.3.35f1",'
                            '"unityVersion":"2021.3.35f1"}', encoding="utf-8")
            with self.assertRaises(ValueError):
                verify(path, "player", "2021.3.35f1")


if __name__ == "__main__":
    unittest.main()
