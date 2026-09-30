import copy
import json
import tempfile
import unittest
from pathlib import Path

import native_scalar_invocation_effects as oracle


class NativeScalarInvocationEffectsOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": "native-scalar-invocation-effects", "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_complete_typed_denominator_and_ordered_failure_effects(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (6, 18))
        rows = {(row.get("operation"), row.get("value"), row["kind"]): row
                for row in self.report["observations"]}
        self.assertEqual(rows[0, False, "other-null"]["markerBits"], 0x3f000000)
        self.assertEqual(rows[0, False, "other-null"]["exception"], "none")
        self.assertEqual(rows[1, True, "target-null"]["first"], oracle.node(7, 11, False))
        self.assertEqual(rows[1, True, "target-null"]["markerBits"], 0x3f800000)
        self.assertEqual(rows[1, True, "other-null"]["first"], oracle.node(8, 11, True))
        self.assertEqual(rows[1, True, "other-null"]["markerBits"], -2147483648)
        self.assertEqual(rows[1, False, "alias"]["first"], oracle.node(8, 12, False))
        self.assertEqual(rows[1, False, "alias"]["first"], rows[1, False, "alias"]["second"])

    def test_bit_pattern_typed_argument_alias_and_effect_order_mutations_reject(self):
        mutations = (
            (0, False, "distinct", None, "markerBits", 0.5),
            (0, True, "distinct", None, "markerBits", 0x3f800000),
            (1, False, "distinct", None, "markerBits", 0),
            (1, False, "distinct", None, "markerBits", 2147483648),
            (0, True, "target-null", None, "markerBits", 0x3f000000),
            (1, True, "target-null", "first", "calls", 8),
            (1, True, "other-null", None, "markerBits", 0x3f800000),
            (1, True, "other-null", "first", "value", False),
            (0, False, "distinct", "first", "value", 0),
            (0, True, "distinct", "first", "value", False),
            (1, False, "distinct", "first", "flushes", 12),
            (1, False, "distinct", "second", "flushes", 17),
            (1, True, "alias", "second", "calls", 7),
            (1, True, "alias", "first", "flushes", 11),
        )
        for operation, value, kind, state, field, replacement in mutations:
            with self.subTest(operation=operation, value=value, kind=kind, field=field):
                changed = copy.deepcopy(self.report)
                row = next(row for row in changed["observations"]
                           if row["kind"] == kind and row.get("operation") == operation and row["value"] is value)
                (row if state is None else row[state])[field] = replacement
                with self.assertRaises(ValueError): self.verify(changed)

    def test_missing_reordered_observations_and_wrong_failure_channel_reject(self):
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError): self.verify(dict(self.report, observations=rows))
        for operation, kind, exception in ((0, "other-null", "System.NullReferenceException"),
                                           (1, "other-null", "none"), (0, "target-null", "none")):
            changed = copy.deepcopy(self.report)
            row = next(row for row in changed["observations"]
                       if row["kind"] == kind and row.get("operation") == operation)
            row["exception"] = exception
            with self.assertRaises(ValueError): self.verify(changed)


if __name__ == "__main__":
    unittest.main()
