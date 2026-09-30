import copy
import json
import tempfile
import unittest
from pathlib import Path

import native_null_checked_invocation as oracle


class NativeInvocationOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": "native-null-checked-invocation", "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_complete_typed_denominator(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (18, 79))

    def test_call_order_snapshot_alias_scalar_width_and_failure_effects_reject(self):
        mutations = (
            (0, "target-null", "holder", "producerCount", 0),
            (6, "target-null", "holder", "afterCount", 1),
            (9, "target-null", "holder", "targetNull", True),
            (9, "success", "second", "calls", 1),
            (2, "success", "first", "flag", 1),
            (1, "alias", "first", "value", 2147483648),
            (8, "repeat", "first", "flag", True),
            (7, "reuse-failure", "first", "calls", 1),
        )
        for operation, kind, state, field, value in mutations:
            with self.subTest(operation=operation, kind=kind, field=field):
                changed = copy.deepcopy(self.report)
                row = next(row for row in changed["observations"]
                           if row["kind"] == kind and row.get("operation") == operation)
                row[state][field] = value
                with self.assertRaises(ValueError): self.verify(changed)

    def test_observation_order_and_coverage_reject(self):
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError): self.verify(dict(self.report, observations=rows))


if __name__ == "__main__":
    unittest.main()
