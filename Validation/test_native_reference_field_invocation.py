import copy
import json
import tempfile
import unittest
from pathlib import Path

from native_reference_field_invocation import observations, verify


class ReferenceFieldInvocationOracleTests(unittest.TestCase):
    def verify_rows(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "behavior.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "stage": "player",
                                        "platform": "WindowsPlayer", "profile": "native-reference-field-invocation",
                                        "observations": rows}))
            return verify(path, "player", "2021.3.35f1")

    def test_observations_preserve_failure_order_aliases_and_counter_width(self):
        rows = observations()
        self.assertEqual(len(rows), 22)
        target_null = next(row for row in rows if row["kind"] == "target-null" and not row["initialFlag"])
        self.assertTrue(target_null["holder"]["flag"])
        self.assertEqual(target_null["holder"]["markerBits"], "00000000")
        self.assertEqual(target_null["first"]["calls"], 7)
        source_null = next(row for row in rows if row["kind"] == "source-null")
        self.assertEqual(source_null["exception"], "none")
        self.assertEqual(source_null["first"]["value"], "null")
        overflow = next(row for row in rows if row["kind"] == "counter-overflow")
        self.assertEqual(overflow["first"]["calls"], -(1 << 31))
        self.assertEqual(self.verify_rows(rows)["observations"], 22)

    def test_missing_prethrow_stores_are_rejected(self):
        rows = copy.deepcopy(observations())
        next(row for row in rows if row["kind"] == "target-null")["holder"]["flag"] = False
        with self.assertRaises(ValueError): self.verify_rows(rows)

    def test_null_reference_argument_is_not_a_receiver_guard(self):
        rows = copy.deepcopy(observations())
        next(row for row in rows if row["kind"] == "source-null")["exception"] = "System.NullReferenceException"
        with self.assertRaises(ValueError): self.verify_rows(rows)

    def test_mutating_the_source_or_wrong_target_is_rejected(self):
        rows = copy.deepcopy(observations())
        next(row for row in rows if row["kind"] == "reuse-second")["first"]["calls"] += 1
        with self.assertRaises(ValueError): self.verify_rows(rows)


if __name__ == "__main__":
    unittest.main()
