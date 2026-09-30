import copy
import json
from pathlib import Path
import tempfile
import unittest

from conditional_call_result_tail import observations, verify


class ConditionalCallResultTailOracleTests(unittest.TestCase):
    def check(self, rows):
        report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                  "profile": "conditional-call-result-tail", "observations": rows}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_repeated_producer_and_second_null_failure_are_required(self):
        self.assertEqual(self.check(observations())["observations"], 52)
        for kind, field, value in (("ready", "producerCount", 1),
                                   ("second-null-ready", "exception", "none"),
                                   ("null-owner", "producerCount", 0)):
            rows = copy.deepcopy(observations())
            row = next(row for row in rows if row["kind"] == kind and row.get("operation") == 0)
            row[field] = value
            with self.subTest(kind=kind, field=field):
                with self.assertRaises(ValueError):
                    self.check(rows)
        rows = copy.deepcopy(observations())
        row = next(row for row in rows if row["kind"] == "ready" and row.get("operation") == 0)
        row["first"]["applyCount"] = 1
        row["second"]["applyCount"] = 0
        with self.assertRaises(ValueError):
            self.check(rows)

    def test_typed_predicate_state_and_reuse_effects_are_required(self):
        for kind, field, value in (("reuse-success", "predicateCount", 1),
                                   ("alias-ready", "value", 1),
                                   ("not-ready", "ready", 0)):
            rows = copy.deepcopy(observations())
            row = next(row for row in rows if row["kind"] == kind and row.get("operation") == 0)
            row["first"][field] = value
            with self.subTest(kind=kind, field=field):
                with self.assertRaises(ValueError):
                    self.check(rows)


if __name__ == "__main__":
    unittest.main()
