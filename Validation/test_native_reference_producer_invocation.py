import copy
import json
from pathlib import Path
import tempfile
import unittest

import native_reference_producer_invocation as oracle


class NativeReferenceProducerInvocationOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                       "profile": "native-reference-producer-invocation", "observations": oracle.observations()}
        self.rows = {(row.get("operation"), row["kind"], row.get("value")): row
                     for row in self.report["observations"]}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_complete_distinct_provider_and_consumer_scope(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (8, 66))
        declaration = self.report["observations"][0]
        self.assertNotEqual(declaration["sourceType"], declaration["resultType"])
        self.assertEqual(declaration["signatures"]["Provider.ReadTarget"], [declaration["resultType"]])
        for operation in range(2):
            for value in (False, True):
                row = self.rows[operation, "success", value]
                self.assertEqual(row["holder"]["source"], "replacement")
                self.assertEqual(row["source"]["reads"], 8)
                self.assertEqual(row["first"], {"calls": 8, "flag": value})
                self.assertEqual(row["second"]["calls"], -13)

    def test_all_null_failures_preserve_their_exact_completed_effects(self):
        for operation in range(2):
            for value in (False, True):
                result_null = self.rows[operation, "result-null", value]
                self.assertEqual(result_null["exception"], "System.NullReferenceException")
                self.assertEqual((result_null["source"]["reads"], result_null["holder"]["beforeCount"]), (8, 1))
                self.assertEqual(result_null["holder"]["source"], "replacement")
                source_null = self.rows[operation, "source-null", value]
                self.assertEqual((source_null["source"]["reads"], source_null["holder"]["beforeCount"]), (7, 0))
                owner_null = self.rows[operation, "source-owner-null", value]
                self.assertEqual((owner_null["source"]["reads"], owner_null["holder"]["beforeCount"]), (8, 0))
                self.assertEqual(owner_null["holder"]["source"], "source")
                for kind in ("replacement-null", "replacement-result-null", "replacement-owner-null"):
                    self.assertEqual(self.rows[operation, kind, value]["exception"], "none")

    def test_provider_node_aliases_wrap_and_reuse_are_independently_observed(self):
        for operation in range(2):
            for value in (False, True):
                aliased_provider = self.rows[operation, "provider-alias", value]
                self.assertEqual(aliased_provider["source"], aliased_provider["replacement"])
                self.assertEqual(aliased_provider["holder"]["source"], "source")
                aliased_node = self.rows[operation, "node-alias", value]
                self.assertEqual(aliased_node["first"], aliased_node["second"])
                overflow = self.rows[operation, "counter-overflow", value]
                self.assertEqual((overflow["source"]["reads"], overflow["first"]["calls"],
                                  overflow["holder"]["beforeCount"]), (-2147483648,) * 3)
                earlier = self.rows[operation, "reuse-success", value]
                later = self.rows[operation, "repeat", not value]
                self.assertEqual(earlier["source"]["reads"], later["source"]["reads"])
                self.assertEqual(earlier["second"]["calls"], -13)
                self.assertEqual(later["replacement"]["reads"], -10)
                self.assertEqual(later["second"], {"calls": -12, "flag": not value})
                self.assertEqual(later["first"], earlier["first"])

    def test_receiver_reloading_failure_order_boolean_width_and_snapshot_mutations_reject(self):
        mutations = (
            ("success", "first", "calls", 7),
            ("success", "second", "calls", -12),
            ("success", "holder", "source", "source"),
            ("result-null", "source", "reads", 7),
            ("result-null", "holder", "beforeCount", 0),
            ("source-null", "holder", "beforeCount", 1),
            ("source-owner-null", "source", "reads", 7),
            ("source-owner-null", "holder", "flag", True),
            ("provider-alias", "replacement", "reads", 7),
            ("node-alias", "second", "calls", 7),
            ("counter-overflow", "holder", "beforeCount", 2147483648),
            ("negative-counters", "first", "calls", -2147483648),
            ("reuse-success", "replacement", "reads", -10),
            ("repeat", "first", "calls", 9),
            ("success", "first", "flag", 0),
        )
        for operation in range(2):
            for kind, state, key, value in mutations:
                with self.subTest(operation=operation, kind=kind, key=key):
                    changed = copy.deepcopy(self.report)
                    row = next(row for row in changed["observations"] if row.get("operation") == operation and row["kind"] == kind)
                    row[state][key] = value
                    with self.assertRaises(ValueError): self.verify(changed)
        changed = copy.deepcopy(self.report)
        changed["observations"][0]["resultType"] = changed["observations"][0]["sourceType"]
        with self.assertRaises(ValueError): self.verify(changed)
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError): self.verify(dict(self.report, observations=rows))


if __name__ == "__main__":
    unittest.main()
