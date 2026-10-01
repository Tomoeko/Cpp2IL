import copy
import json
import tempfile
import unittest
from pathlib import Path

from native_nested_reference_field_invocation import observations, verify


class NestedReferenceFieldInvocationOracleTests(unittest.TestCase):
    def verify_rows(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "behavior.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "stage": "player",
                                        "platform": "WindowsPlayer", "profile": "native-nested-reference-field-invocation",
                                        "observations": rows}))
            return verify(path, "player", "2021.3.35f1")

    def test_null_owner_is_distinct_from_a_null_payload(self):
        rows = observations()
        self.assertEqual(len(rows), 22)
        for kind in ("source-null", "target-null", "both-null", "holder-null"):
            row = next(row for row in rows if row["kind"] == kind)
            self.assertEqual(row["exception"], "System.NullReferenceException")
            self.assertEqual(row["first"]["calls"], 7)
        payload = next(row for row in rows if row["kind"] == "payload-null")
        self.assertEqual(payload["exception"], "none")
        self.assertEqual(payload["first"], {"calls": 8, "value": "null"})
        self.assertEqual(self.verify_rows(rows)["observations"], 22)

    def test_partial_call_effects_on_null_failure_are_rejected(self):
        rows = copy.deepcopy(observations())
        next(row for row in rows if row["kind"] == "source-null")["first"]["calls"] += 1
        with self.assertRaises(ValueError): self.verify_rows(rows)

    def test_a_null_payload_cannot_become_a_receiver_check(self):
        rows = copy.deepcopy(observations())
        next(row for row in rows if row["kind"] == "payload-null")["exception"] = "System.NullReferenceException"
        with self.assertRaises(ValueError): self.verify_rows(rows)

    def test_reuse_must_read_the_shared_source_and_preserve_counter_width(self):
        rows = observations()
        overflow = next(row for row in rows if row["kind"] == "counter-overflow")
        self.assertEqual(overflow["first"]["calls"], -(1 << 31))
        shared = next(row for row in rows if row["kind"] == "shared-source-second")
        self.assertEqual(shared["second"]["value"], "second")
        rows = copy.deepcopy(rows)
        next(row for row in rows if row["kind"] == "shared-source-second")["second"]["value"] = "first"
        with self.assertRaises(ValueError): self.verify_rows(rows)


if __name__ == "__main__":
    unittest.main()
