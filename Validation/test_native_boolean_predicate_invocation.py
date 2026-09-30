import copy
import json
import tempfile
import unittest
from pathlib import Path

import native_boolean_predicate_invocation as oracle


class NativeBooleanPredicateInvocationOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": oracle.PROFILE, "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def row(self, operation, kind, initial=False):
        return next(row for row in self.report["observations"] if row.get("operation") == operation and
                    row["kind"] == kind and row["initialFlag"] is initial)

    def test_aliases_observe_the_live_field_after_the_first_call(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (6, 82))
        for initial in (False, True):
            row = self.row("complement-live", "self-first", initial)
            self.assertEqual(row["owner"]["flag"], not initial)
            self.assertEqual(row["second"]["flag"], not initial)
            self.assertEqual(row["owner"]["calls"], 4)
            row = self.row("live-pair", "shared-target", initial)
            self.assertEqual(row["first"]["calls"], 9)
            self.assertEqual(row["first"]["flag"], not initial)

    def test_failures_retain_only_the_effects_that_precede_them(self):
        for initial in (False, True):
            first = self.row("stored-pair", "first-null", initial)
            self.assertEqual(first["owner"]["flag"], not initial)
            self.assertEqual(first["first"]["calls"], 7)
            second = self.row("live-pair", "second-null", initial)
            self.assertEqual(second["first"]["calls"], 8)
            self.assertEqual(second["first"]["flag"], initial)
            self.assertEqual(second["second"]["calls"], 11)
            self.assertEqual(second["exception"], "System.NullReferenceException")
            self.assertEqual(self.row("negated", "second-null", initial)["exception"], "none")
            self.assertEqual(self.row("complement-live", "owner-null", initial)["first"]["calls"], 7)

    def test_order_alias_type_and_missing_observation_mutations_reject(self):
        mutations = (
            ("complement-live", "self-first", "second", "flag", False),
            ("stored-pair", "first-null", "owner", "flag", False),
            ("live-pair", "second-null", "first", "calls", 7),
            ("live-pair", "shared-target", "first", "calls", 8),
            ("complement-live", "reuse-success", "second", "flag", 1),
            ("negated", "self-first", "owner", "first", "first"),
        )
        for operation, kind, name, field, replacement in mutations:
            with self.subTest(operation=operation, kind=kind, field=field):
                changed = copy.deepcopy(self.report)
                row = next(row for row in changed["observations"] if row.get("operation") == operation and
                           row["kind"] == kind and row["initialFlag"] is False)
                row[name][field] = replacement
                with self.assertRaises(ValueError): self.verify(changed)
        changed = copy.deepcopy(self.report)
        changed["observations"][0]["parameterType"] = "System.Byte"
        with self.assertRaises(ValueError): self.verify(changed)
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError): self.verify(dict(self.report, observations=rows))


if __name__ == "__main__":
    unittest.main()
