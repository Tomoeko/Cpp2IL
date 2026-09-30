import copy
import json
import tempfile
import unittest
from pathlib import Path

import native_derived_receiver_invocation as oracle


class NativeDerivedReceiverInvocationOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": "native-derived-receiver-invocation", "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_complete_denominator_and_distinct_derived_receiver_effects(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (7, 18))
        rows = {(row.get("operation"), row["kind"]): row for row in self.report["observations"]}
        self.assertEqual(rows[0, "distinct-target"]["first"], {"calls": 0, "value": 17, "flag": False, "extra": 31})
        self.assertEqual(rows[0, "distinct-target"]["second"], {"calls": 1, "value": -2147483648, "flag": True, "extra": -37})
        self.assertEqual(rows[1, "distinct-target"]["second"], {"calls": 1, "value": -19, "flag": False, "extra": -37})
        self.assertEqual(rows[0, "repeat"]["first"]["value"], 2147483647)
        self.assertEqual(rows[1, "repeat"]["first"]["flag"], False)
        self.assertEqual(rows[1, "alias"]["first"], rows[1, "alias"]["second"])
        self.assertEqual(rows[0, "reuse-success"]["holder"]["targetRuntimeType"], oracle.NAMESPACE + "DerivedNode")

    def test_receiver_member_identity_scalar_type_and_preserved_fields_mutations_reject(self):
        for field, replacement in (("baseType", oracle.NAMESPACE + "DerivedNode"),
                                    ("targetType", oracle.NAMESPACE + "BaseNode"),
                                    ("integerDeclaringType", oracle.NAMESPACE + "DerivedNode"),
                                    ("booleanParameter", "System.Byte"),
                                    ("extraDeclaringType", oracle.NAMESPACE + "BaseNode")):
            changed = copy.deepcopy(self.report)
            changed["observations"][0][field] = replacement
            with self.assertRaises(ValueError): self.verify(changed)
        mutations = (
            (0, "distinct-target", "first", "calls", 1),
            (0, "distinct-target", "second", "value", 2147483648),
            (0, "alias", "second", "value", 17),
            (1, "success", "first", "flag", 1),
            (1, "success", "first", "value", 29),
            (0, "success", "first", "extra", 0),
            (1, "distinct-target", "second", "extra", 31),
            (0, "target-null", "first", "calls", 1),
            (1, "repeat", "first", "calls", 1),
            (1, "repeat", "first", "flag", True),
            (0, "reuse-success", "holder", "targetRuntimeType", oracle.NAMESPACE + "BaseNode"),
            (0, "distinct-target", "holder", "targetFirst", True),
        )
        for operation, kind, state, field, replacement in mutations:
            with self.subTest(operation=operation, kind=kind, field=field):
                changed = copy.deepcopy(self.report)
                row = next(row for row in changed["observations"]
                           if row["kind"] == kind and row.get("operation") == operation)
                row[state][field] = replacement
                with self.assertRaises(ValueError): self.verify(changed)

    def test_missing_reordered_observations_and_wrong_null_failures_reject(self):
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError): self.verify(dict(self.report, observations=rows))
        changed = copy.deepcopy(self.report)
        row = next(row for row in changed["observations"] if row["kind"] == "holder-null")
        row["exception"] = "none"
        with self.assertRaises(ValueError): self.verify(changed)


if __name__ == "__main__":
    unittest.main()
