import copy
import json
from pathlib import Path
import tempfile
import unittest

import native_scalar_producer_invocation as oracle


class NativeScalarProducerInvocationOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                       "profile": "native-scalar-producer-invocation", "observations": oracle.observations()}
        self.rows = {(row.get("operation"), row["kind"]): row for row in self.report["observations"]}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_complete_typed_scope_and_counter_wrap(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (7, 34))
        for operation in range(2):
            row = self.rows[operation, "counter-overflow"]
            self.assertEqual(row["source"]["reads"], -2147483648)
            self.assertEqual(row["target"]["calls"], -2147483648)
            self.assertEqual(row["holder"]["beforeCount"], -2147483648)

    def test_saved_consumer_and_producer_failure_order_are_observable(self):
        for operation in range(2):
            success = self.rows[operation, "success"]
            self.assertEqual(success["holder"]["target"], "replacement")
            self.assertEqual(success["target"]["value"], 29)
            self.assertEqual(success["replacement"]["calls"], -29)
            absent_target = self.rows[operation, "target-null"]
            self.assertEqual(absent_target["exception"], "System.NullReferenceException")
            self.assertEqual(absent_target["source"]["reads"], 8)
            self.assertEqual(absent_target["holder"]["beforeCount"], 1)
            self.assertEqual(absent_target["holder"]["target"], "replacement")
            self.assertEqual(absent_target["replacement"]["calls"], -29)
            absent_source = self.rows[operation, "source-null"]
            self.assertEqual(absent_source["source"]["reads"], 7)
            self.assertEqual(absent_source["holder"]["beforeCount"], 0)
            absent_owner = self.rows[operation, "source-owner-null"]
            self.assertEqual(absent_owner["source"]["reads"], 8)
            self.assertEqual(absent_owner["holder"]["beforeCount"], 0)
            self.assertEqual(self.rows[operation, "replacement-null"]["exception"], "none")

    def test_aliases_and_reuse_preserve_independent_snapshots(self):
        for operation in range(2):
            alias = self.rows[operation, "source-target-alias"]
            self.assertEqual(alias["source"], alias["target"])
            self.assertEqual(alias["source"]["reads"], 8)
            self.assertEqual(alias["source"]["calls"], 12)
            earlier = self.rows[operation, "reuse-success"]
            later = self.rows[operation, "repeat"]
            self.assertEqual(earlier["source"]["reads"], 9)
            self.assertEqual(earlier["target"]["value"], -2147483648)
            self.assertEqual(earlier["replacement"]["calls"], -29)
            self.assertEqual(later["source"]["reads"], 10)
            self.assertEqual(later["target"]["value"], -2147483648)
            self.assertEqual(later["replacement"]["calls"], -28)
            self.assertEqual(later["replacement"]["value"], 2147483647)

    def test_capture_null_order_typed_value_and_alias_mutations_reject(self):
        mutations = (
            ("success", "target", "value", -19),
            ("success", "replacement", "calls", -28),
            ("success", "holder", "target", "target"),
            ("target-null", "source", "reads", 7),
            ("target-null", "holder", "beforeCount", 0),
            ("source-null", "holder", "beforeCount", 1),
            ("source-owner-null", "source", "reads", 7),
            ("source-owner-null", "holder", "flag", True),
            ("source-target-alias", "target", "calls", 11),
            ("minimum", "target", "value", 2147483648),
            ("counter-overflow", "holder", "beforeCount", 2147483648),
            ("reuse-success", "replacement", "value", -2147483648),
            ("repeat", "replacement", "value", -2147483648),
            ("success", "holder", "flag", 1),
        )
        for operation in range(2):
            for kind, state, key, value in mutations:
                with self.subTest(operation=operation, kind=kind, key=key):
                    changed = copy.deepcopy(self.report)
                    row = next(row for row in changed["observations"]
                               if row.get("operation") == operation and row["kind"] == kind)
                    row[state][key] = value
                    with self.assertRaises(ValueError):
                        self.verify(changed)
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError):
                self.verify(dict(self.report, observations=rows))
        changed = copy.deepcopy(self.report)
        changed["observations"][0]["signatures"]["Node.ReadValue"] = ["System.UInt32"]
        with self.assertRaises(ValueError):
            self.verify(changed)


if __name__ == "__main__":
    unittest.main()
