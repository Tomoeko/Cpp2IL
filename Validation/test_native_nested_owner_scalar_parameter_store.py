import json
from pathlib import Path
import tempfile
import unittest

import native_nested_owner_scalar_parameter_store as oracle


class NestedOwnerScalarParameterStoreOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                       "profile": oracle.PROFILE, "observations": oracle.observations()}

    def check(self, accepts=False):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(self.report), encoding="utf-8")
            if accepts:
                result = oracle.verify(path, "player", "2021.3.35f1")
                self.assertEqual((result["methods"], result["observations"]), (3, 48))
            else:
                with self.assertRaises(ValueError): oracle.verify(path, "player", "2021.3.35f1")

    def row(self, kind):
        return next(row for row in self.report["observations"] if row["kind"] == kind)

    def test_exact_nested_scope_is_accepted(self):
        self.check(True)

    def test_original_enclosing_identity_and_visibility_are_required(self):
        self.row("declarations")["enclosing"]["Holder"] = "none"
        self.check()
        self.setUp()
        self.row("declarations")["visibility"]["Holder"] = 1
        self.check()

    def test_numeric_visibility_preserves_top_level_and_nested_access(self):
        for owner, changed in (("Cell", 2), ("Container", 0), ("Holder", 3)):
            with self.subTest(owner=owner):
                self.setUp()
                self.row("declarations")["visibility"][owner] = changed
                self.check()

    def test_null_paths_preserve_neighbors_and_both_targets(self):
        self.row("null-target-9")["after"]["second"]["amountBits"] = 0
        self.check()

    def test_ieee_payloads_cannot_be_canonicalized(self):
        self.row("value-11")["after"]["first"]["amountBits"] = 0x7fc00000
        self.check()

    def test_retargeted_alias_effect_cannot_hit_old_target(self):
        row = self.row("retarget-first")
        row["after"]["first"]["amountBits"] = row["incoming"]
        self.check()

    def test_full_denominator_and_order_are_required(self):
        self.report["observations"].pop()
        self.check()
        self.setUp()
        self.report["observations"][-1], self.report["observations"][-2] = self.report["observations"][-2], self.report["observations"][-1]
        self.check()

    def test_editor_and_player_transport_are_explicit_and_keep_all_rows(self):
        editor = oracle.observations("editor")
        player = oracle.observations("player")
        for rows, expected in ((editor, 0x7fc00002), (player, 0x7f800002)):
            for kind in ("value-11", "restore-first"):
                row = next(row for row in rows if row["kind"] == kind)
                self.assertEqual(row["incoming"], 0x7f800002)
                self.assertEqual(row["transportedIncomingBits"], expected)
                self.assertEqual(row["after"]["first"]["amountBits"], expected)
            self.assertEqual(len(rows), 48)

    def test_transport_observation_cannot_hide_a_native_payload_change(self):
        self.row("null-owner-11")["transportedIncomingBits"] ^= 1
        self.check()


if __name__ == "__main__":
    unittest.main()
