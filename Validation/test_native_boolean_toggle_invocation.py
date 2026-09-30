import copy
import json
import tempfile
import unittest
from pathlib import Path

import native_boolean_toggle_invocation as oracle


class NativeBooleanToggleInvocationOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": oracle.PROFILE, "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_owner_toggle_precedes_null_failure_and_reuse_keeps_order(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (4, 18))
        rows = {(row.get("initialFlag"), row["kind"]): row for row in self.report["observations"]}
        for initial in (False, True):
            failed = rows[initial, "target-null"]
            self.assertEqual(failed["holder"]["flag"], not initial)
            self.assertEqual(failed["node"], {"calls": 7, "flag": initial})
            self.assertEqual(failed["exception"], "System.NullReferenceException")
            self.assertEqual(rows[initial, "holder-null"]["node"], {"calls": 7, "flag": initial})
            self.assertEqual(rows[initial, "reuse-success"]["node"], {"calls": 8, "flag": initial})
            self.assertEqual(rows[initial, "repeat"]["node"], {"calls": 9, "flag": not initial})
            shared = rows[initial, "shared-target-second"]
            self.assertTrue(shared["sharedTarget"])
            self.assertEqual(shared["node"], {"calls": 9, "flag": initial})
            self.assertEqual(shared["holder"]["flag"], initial)
            self.assertEqual(shared["otherHolder"]["flag"], not initial)

    def test_store_order_callee_identity_type_and_alias_mutations_reject(self):
        mutations = (
            ("target-null", "holder", "flag", False),
            ("target-null", "node", "calls", 8),
            ("success", "node", "flag", False),
            ("success", "holder", "targetNode", False),
            ("reuse-success", "node", "calls", 9),
            ("repeat", "holder", "flag", 1),
            ("shared-target-second", "otherHolder", "flag", False),
        )
        for kind, owner, field, replacement in mutations:
            with self.subTest(kind=kind, owner=owner, field=field):
                changed = copy.deepcopy(self.report)
                row = next(row for row in changed["observations"]
                           if row["kind"] == kind and row.get("initialFlag") is False)
                row[owner][field] = replacement
                with self.assertRaises(ValueError): self.verify(changed)
        changed = copy.deepcopy(self.report)
        changed["observations"][0]["parameterType"] = "System.Byte"
        with self.assertRaises(ValueError): self.verify(changed)
        changed = copy.deepcopy(self.report)
        row = next(row for row in changed["observations"] if row["kind"] == "shared-target-first")
        row["sharedTarget"] = False
        with self.assertRaises(ValueError): self.verify(changed)

    def test_missing_reordered_observations_and_null_success_reject(self):
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError): self.verify(dict(self.report, observations=rows))
        changed = copy.deepcopy(self.report)
        row = next(row for row in changed["observations"] if row["kind"] == "holder-null")
        row["exception"] = "none"
        with self.assertRaises(ValueError): self.verify(changed)


if __name__ == "__main__":
    unittest.main()
