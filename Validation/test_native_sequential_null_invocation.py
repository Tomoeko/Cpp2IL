import copy
import json
import tempfile
import unittest
from pathlib import Path

import native_sequential_null_invocation as oracle


class SequentialInvocationOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": "native-sequential-null-invocation", "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_complete_typed_denominator(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (15, 74))

    def test_null_timing_alias_snapshot_control_and_width_mutations_reject(self):
        mutations = (
            (0, "first-null", "first", "calls", 1),
            (4, "second-null", "first", "calls", 0),
            (4, "second-null", "holder", "beforeCount", 0),
            (4, "second-null", "holder", "afterCount", 1),
            (5, "second-null", "first", "calls", 1),
            (5, "success", "second", "value", 29),
            (3, "query-false", "first", "calls", 2),
            (2, "alias", "first", "value", 2147483648),
            (1, "success", "first", "flag", 1),
            (7, "repeat", "first", "calls", 5),
        )
        for operation, kind, state, field, value in mutations:
            with self.subTest(operation=operation, kind=kind, field=field):
                changed = copy.deepcopy(self.report)
                row = next(row for row in changed["observations"]
                           if row["kind"] == kind and row.get("operation") == operation)
                row[state][field] = value
                with self.assertRaises(ValueError):
                    self.verify(changed)

    def test_coverage_order_profile_and_failure_channel_reject(self):
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError):
                self.verify(dict(self.report, observations=rows))
        with self.assertRaises(ValueError):
            self.verify(dict(self.report, profile="native-null-checked-invocation"))
        changed = copy.deepcopy(self.report)
        row = next(row for row in changed["observations"]
                   if row["kind"] == "second-null" and row.get("operation") == 4)
        row["exception"] = "none"
        with self.assertRaises(ValueError):
            self.verify(changed)


if __name__ == "__main__":
    unittest.main()
