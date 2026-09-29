import json
from pathlib import Path
import tempfile
import unittest

import final_interface_boolean_getter as oracle


class FinalInterfaceBooleanGetterOracleTests(unittest.TestCase):
    def verify(self, rows, **headers):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "behavior.json"
            report = {"unityVersion": "2021.3.35f1", "stage": "player",
                      "platform": "WindowsPlayer", "profile": "final-interface-boolean-getter",
                      "observations": rows}
            report.update(headers)
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_complete_dispatch_matrix_has_eleven_declarations_and_229_observations(self):
        result = self.verify(oracle.observations())
        self.assertEqual(result["methods"], 11)
        self.assertEqual(result["observations"], 229)

    def test_inherited_interface_slots_cannot_follow_new_shadow_members(self):
        rows = oracle.observations()
        for route in ("interface-value", "interface-read", "base-value", "base-read"):
            with self.subTest(route=route):
                row = next(row for row in rows if row.get("owner") == "shadow" and
                           row.get("route") == route and row["flagBefore"] != row["shadowFlagBefore"])
                original = row["result"]
                row["result"] = row["shadowFlagBefore"]
                with self.assertRaises(ValueError):
                    self.verify(rows)
                row["result"] = original

    def test_shadow_reads_cannot_follow_base_slots(self):
        for route in ("shadow-value", "shadow-read"):
            with self.subTest(route=route):
                rows = oracle.observations()
                row = next(row for row in rows if row.get("route") == route and
                           row.get("kind") == "read" and row["flagBefore"] != row["shadowFlagBefore"])
                row["result"] = row["flagBefore"]
                with self.assertRaises(ValueError):
                    self.verify(rows)

    def test_explicit_interface_getters_return_their_own_field_without_failure(self):
        for route in oracle.EXPLICIT_ROUTES:
            with self.subTest(route=route):
                rows = oracle.observations()
                row = next(row for row in rows if row.get("owner") == "explicit" and row["route"] == route)
                row["result"] = not row["flagBefore"]
                with self.assertRaises(ValueError):
                    self.verify(rows)
                row["result"] = row["flagBefore"]
                row["failure"] = "System.InvalidCastException"
                with self.assertRaises(ValueError):
                    self.verify(rows)

    def test_reads_preserve_all_neighbor_state_and_reference_identity(self):
        changes = {"flagAfter": True, "neighborAfter": 1, "referenceSame": False,
                   "shadowFlagAfter": True, "shadowNeighborAfter": 1, "shadowReferenceSame": False}
        for key, value in changes.items():
            with self.subTest(key=key):
                rows = oracle.observations()
                row = next(row for row in rows if row.get("owner") == "shadow" and row.get("kind") == "read")
                row[key] = value
                with self.assertRaises(ValueError):
                    self.verify(rows)

    def test_every_null_dispatch_path_requires_the_exception_and_no_result(self):
        for owner, routes in (("ordinary", oracle.ORDINARY_ROUTES),
                              ("explicit", oracle.EXPLICIT_ROUTES), ("shadow", oracle.SHADOW_ROUTES)):
            for route in routes:
                with self.subTest(owner=owner, route=route):
                    rows = oracle.observations()
                    row = next(row for row in rows if row.get("owner") == owner and
                               row.get("kind") == "null" and row["route"] == route)
                    row["failure"] = "none"
                    row["result"] = False
                    with self.assertRaises(ValueError):
                        self.verify(rows)

    def test_numeric_one_is_not_an_observed_boolean(self):
        rows = oracle.observations()
        row = next(row for row in rows if row.get("kind") == "read" and row["result"] is True)
        row["result"] = 1
        with self.assertRaises(ValueError):
            self.verify(rows)

    def test_missing_rows_and_wrong_target_cannot_pass(self):
        with self.assertRaises(ValueError):
            self.verify(oracle.observations()[:-1])
        for key, value in (("unityVersion", "2021.3.34f1"), ("platform", "OSXPlayer"),
                           ("stage", "editor"), ("profile", "boolean-getter")):
            with self.subTest(key=key):
                with self.assertRaises(ValueError):
                    self.verify(oracle.observations(), **{key: value})


if __name__ == "__main__":
    unittest.main()
