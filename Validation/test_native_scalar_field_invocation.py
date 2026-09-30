import copy
import json
import tempfile
import unittest
from pathlib import Path

import native_scalar_field_invocation as oracle


class NativeScalarFieldInvocationOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": "native-scalar-field-invocation", "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_complete_typed_denominator(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (12, 58))

    def test_captured_and_live_field_contracts_have_distinct_values(self):
        rows = {(row.get("operation"), row["kind"]): row for row in self.report["observations"]}
        self.assertEqual(rows[5, "success"]["first"], {"calls": 1, "value": 29, "flag": True})
        self.assertEqual(rows[6, "success"]["first"], {"calls": 1, "value": 43, "flag": False})
        self.assertEqual(rows[1, "reuse-success"]["first"]["value"], -2147483648)
        self.assertEqual(rows[2, "reuse-success"]["first"]["value"], 2147483647)
        self.assertEqual(rows[5, "target-null"]["holder"]["beforeCount"], 1)
        self.assertEqual(rows[5, "target-null"]["holder"]["afterCount"], 0)

    def test_argument_capture_width_alias_and_failure_effect_mutations_reject(self):
        mutations = (
            (0, "success", "first", "flag", True),
            (1, "reuse-success", "first", "value", 2147483647),
            (2, "alias", "first", "value", 2147483648),
            (3, "success", "first", "flag", 1),
            (4, "target-null", "holder", "afterCount", 1),
            (5, "success", "first", "value", 43),
            (5, "success", "first", "flag", False),
            (6, "success", "first", "value", 29),
            (5, "target-null", "holder", "beforeCount", 0),
            (6, "target-null", "holder", "flag", False),
            (5, "replacement-null", "first", "calls", 0),
            (5, "success", "second", "calls", 1),
            (6, "alias", "second", "calls", 0),
            (6, "repeat", "holder", "beforeCount", 1),
            (5, "repeat", "first", "flag", True),
        )
        for operation, kind, state, field, value in mutations:
            with self.subTest(operation=operation, kind=kind, field=field):
                changed = copy.deepcopy(self.report)
                row = next(row for row in changed["observations"]
                           if row["kind"] == kind and row.get("operation") == operation)
                row[state][field] = value
                with self.assertRaises(ValueError): self.verify(changed)

    def test_coverage_order_and_failure_channel_mutations_reject(self):
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError): self.verify(dict(self.report, observations=rows))
        changed = copy.deepcopy(self.report)
        row = next(row for row in changed["observations"] if row["kind"] == "holder-null")
        row["exception"] = "none"
        with self.assertRaises(ValueError): self.verify(changed)


if __name__ == "__main__":
    unittest.main()
