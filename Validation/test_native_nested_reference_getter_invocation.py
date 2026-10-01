import copy
import json
import tempfile
import unittest
from pathlib import Path

from native_nested_reference_getter_invocation import PROFILES, observations, verify_variant


class NestedReferenceGetterInvocationOracleTests(unittest.TestCase):
    def verify_rows(self, rows, variant="base"):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "behavior.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                                        "profile": PROFILES[variant][0], "observations": rows}))
            return verify_variant(path, "player", "2021.3.35f1", variant)

    def test_whole_assembly_and_protected_readonly_getters_have_explicit_denominators(self):
        for variant, count in (("base", 26), ("folded", 30), ("ambiguous", 29)):
            rows = observations(variant)
            self.assertEqual(len(rows), count)
            self.assertEqual(rows[0]["fieldAccess"]["SourceOwner._payload"], "Family")
            self.assertTrue(rows[0]["readOnlyProperties"])
            self.assertEqual(self.verify_rows(rows, variant)["observations"], count)

    def test_null_getter_receiver_is_distinct_from_a_returned_null_payload(self):
        rows = observations()
        getter_null = next(row for row in rows if row["kind"] == "getter-null")
        self.assertEqual(getter_null["exception"], "System.NullReferenceException")
        self.assertEqual(getter_null["payload"], "not-returned")
        accepted_null = next(row for row in rows if row["kind"] == "payload-null")
        self.assertEqual(accepted_null["exception"], "none")
        self.assertEqual(accepted_null["first"], {"calls": 8, "value": "null"})
        accepted_null["exception"] = "System.NullReferenceException"
        with self.assertRaises(ValueError): self.verify_rows(rows)

    def test_null_owner_cannot_produce_partial_consumer_effects(self):
        rows = observations()
        for kind in ("source-null", "target-null", "both-null", "holder-null"):
            changed = copy.deepcopy(rows)
            next(row for row in changed if row["kind"] == kind)["first"]["calls"] += 1
            with self.assertRaises(ValueError): self.verify_rows(changed)

    def test_getter_results_preserve_identity_on_shared_source_reuse(self):
        rows = observations()
        self.assertEqual(next(row for row in rows if row["kind"] == "counter-overflow")["first"]["calls"], -(1 << 31))
        changed = copy.deepcopy(rows)
        next(row for row in changed if row["kind"] == "shared-source-second")["second"]["value"] = "first"
        with self.assertRaises(ValueError): self.verify_rows(changed)

    def test_unrelated_folded_getter_storage_cannot_replace_the_live_receiver(self):
        rows = observations("folded")
        self.assertEqual(next(row for row in rows if row["kind"] == "getter-first")["payload"], "first")
        self.assertEqual(next(row for row in rows if row["kind"] == "mirror-getter-second")["payload"], "second")
        changed = copy.deepcopy(rows)
        next(row for row in changed if row["kind"] == "success")["first"]["value"] = "second"
        with self.assertRaises(ValueError): self.verify_rows(changed, "folded")
        changed = copy.deepcopy(rows)
        next(row for row in changed if row["kind"] == "success")["mirrorSource"] = "first"
        with self.assertRaises(ValueError): self.verify_rows(changed, "folded")

    def test_ambiguity_scope_retains_both_getters_and_all_original_behavior(self):
        rows = observations("ambiguous")
        self.assertIn("SourceOwner.get_OtherPayload", rows[0]["signatures"])
        self.assertEqual(next(row for row in rows if row["kind"] == "other-getter-second")["payload"], "second")
        changed = copy.deepcopy(rows)
        changed[0]["signatures"].pop("SourceOwner.get_OtherPayload")
        with self.assertRaises(ValueError): self.verify_rows(changed, "ambiguous")


if __name__ == "__main__":
    unittest.main()
