import copy
import json
from pathlib import Path
import tempfile
import unittest

import reference_array_search as oracle


class ReferenceArraySearchOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                       "profile": "reference-array-search", "observations": oracle.observations()}
        self.rows = {(row.get("layout"), row.get("operation"), row["kind"], row.get("key")): row
                     for row in self.report["observations"]}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_full_scope_preserves_first_match_nulls_and_signed_boundaries(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (8, 202))
        self.assertEqual(self.report["observations"][0]["fields"], 7)
        for layout in ("simple", "padded"):
            self.assertEqual(self.rows[layout, "find", "duplicate", 1]["result"], 0)
            self.assertEqual(self.rows[layout, "find", "alias", 1]["result"], 0)
            self.assertEqual(self.rows[layout, "find", "tail-match", 1]["result"], 3)
            self.assertEqual(self.rows[layout, "find", "extremes", -(1 << 31)]["result"], 0)
            self.assertEqual(self.rows[layout, "find", "extremes", (1 << 31) - 1]["result"], 1)
            for kind in ("null-array", "empty", "all-null"):
                self.assertEqual(self.rows[layout, "find", kind, 0]["result"], -1)
                self.assertIs(self.rows[layout, "contains", kind, 0]["result"], False)
                self.assertEqual(self.rows[layout, "find", kind, 0]["exception"], "none")
            self.assertIs(self.rows[layout, "contains", "distinct", 0]["result"], True)
            self.assertIsNone(self.rows[layout, "find", "holder-null", 0]["result"])
            self.assertEqual(self.rows[layout, "find", "holder-null", 0]["exception"], "System.NullReferenceException")

    def test_repeats_retain_array_aliases_and_padded_neighbor_contents(self):
        for layout in ("simple", "padded"):
            for operation in ("find", "contains"):
                row = self.rows[layout, operation, "alias", 1]
                self.assertEqual(row["itemsBefore"], row["itemsAfter"])
                self.assertEqual(row["result"], row["repeatedResult"])
                self.assertEqual(row["itemsAfter"][0], row["itemsAfter"][2])
                self.assertEqual(row["neighbor"], 43)
                if layout == "padded":
                    self.assertEqual(row["itemsAfter"][2]["tag"], oracle.TAG)
                    self.assertEqual(row["itemsAfter"][2]["text"], "item-0")

    def test_wrong_match_null_order_boolean_width_or_mutation_rejects(self):
        changes = (
            ("find", "duplicate", 1, "result", 1),
            ("find", "alias", 1, "repeatedResult", 2),
            ("find", "extremes", -(1 << 31), "result", -1),
            ("find", "null-array", 0, "exception", "System.NullReferenceException"),
            ("find", "holder-null", 0, "result", -1),
            ("contains", "distinct", 0, "result", 0),
            ("contains", "empty", 0, "result", True),
            ("find", "distinct", 1, "result", 1.0),
            ("find", "negative", -1, "neighbor", 0),
        )
        for layout in ("simple", "padded"):
            for operation, kind, key, field, value in changes:
                with self.subTest(layout=layout, operation=operation, kind=kind, field=field):
                    changed = copy.deepcopy(self.report)
                    row = next(row for row in changed["observations"] if row.get("layout") == layout and
                               row.get("operation") == operation and row["kind"] == kind and row["key"] == key)
                    row[field] = value
                    with self.assertRaises(ValueError): self.verify(changed)
        for field, value in (("identity", 2), ("key", 0), ("tag", "00000000-0000-0000-0000-000000000000"),
                             ("text", "item-2")):
            changed = copy.deepcopy(self.report)
            row = next(row for row in changed["observations"] if row.get("layout") == "padded" and
                       row.get("operation") == "find" and row["kind"] == "alias" and row["key"] == 1)
            row["itemsAfter"][2][field] = value
            with self.assertRaises(ValueError): self.verify(changed)

    def test_declaration_and_order_changes_reject(self):
        for field, value in (("methods", 7), ("fields", 6), ("paddedKeyType", "System.UInt32"),
                             ("paddedArrayType", "ReferenceArraySearchFixture.Entry[]")):
            changed = copy.deepcopy(self.report)
            changed["observations"][0][field] = value
            with self.assertRaises(ValueError): self.verify(changed)
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError): self.verify(dict(self.report, observations=rows))


if __name__ == "__main__":
    unittest.main()
