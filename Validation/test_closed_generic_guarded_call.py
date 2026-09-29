import json
import tempfile
import unittest
from pathlib import Path

from closed_generic_guarded_call import COUNTERS, DELTAS, observations, signed32, verify


class ClosedGenericGuardedCallOracleTests(unittest.TestCase):
    def check_report(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                  "profile": "closed-generic-guarded-call", "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_denominator_and_signed_overflow(self):
        rows = observations()
        self.assertEqual(len(rows), 311)
        self.assertEqual(self.check_report(rows)["methods"], 4)
        self.assertEqual(len(DELTAS), 11)
        self.assertEqual(len(COUNTERS), 5)
        self.assertEqual(signed32((1 << 31) - 1 + 1), -(1 << 31))
        self.assertEqual(signed32(-(1 << 31) - 1), (1 << 31) - 1)
        self.assertEqual(sum(row["kind"] == "call" for row in rows), 220)
        self.assertEqual(sum(row["kind"] == "repeat" for row in rows), 55)

    def test_sink_null_fails_after_add_effect(self):
        rows = observations()
        row = next(row for row in rows if row["kind"] == "call" and row["sinkNull"]
                   and row["counterBefore"] == (1 << 31) - 1 and row["delta"] == 1)
        self.assertEqual(row["counterAfter"], -(1 << 31))
        self.assertEqual(row["failure"], "System.NullReferenceException")
        row["counterAfter"] = (1 << 31) - 1
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_repeated_call_order_and_second_result(self):
        rows = observations()
        row = next(row for row in rows if row["kind"] == "repeat" and row["delta"] == 1
                   and row["counterBefore"] == -(1 << 31))
        self.assertEqual(row["secondCounter"], (1 << 31) - 1)
        row["secondSink"] = row["firstSink"]
        with self.assertRaises(ValueError):
            self.check_report(rows)

    def test_reference_identity_and_neighbor_changes_are_rejected(self):
        for kind, field, value in (("call", "tagSame", False),
                                   ("call", "sinkReferenceSame", False),
                                   ("call", "sinkNeighborAfter", 0),
                                   ("repeat", "tagSame", False),
                                   ("object-instantiation", "tagSame", False)):
            rows = observations()
            row = next(row for row in rows if row["kind"] == kind and
                       (kind != "call" or not row["sinkNull"]))
            row[field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_receiver_null_precedes_sink_effect_and_numeric_types_are_strict(self):
        for field, value in (("failure", "none"), ("sinkLastAfter", 0)):
            rows = observations()
            row = next(row for row in rows if row["kind"] == "null-receiver" and not row["sinkNull"])
            row[field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)
        for field, value in (("value", False), ("counterAfter", float(-(1 << 31)))):
            rows = observations()
            row = next(row for row in rows if row["kind"] == "call" and not row["sinkNull"])
            row[field] = value
            with self.assertRaises(ValueError):
                self.check_report(rows)

    def test_wrong_stage_profile_or_row_count_is_rejected(self):
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check_report(rows)
        for change in ({"stage": "editor"}, {"platform": "OSXPlayer"},
                       {"profile": "guarded-scalar-accessor"}):
            with self.assertRaises(ValueError):
                self.check_report(observations(), **change)


if __name__ == "__main__":
    unittest.main()
