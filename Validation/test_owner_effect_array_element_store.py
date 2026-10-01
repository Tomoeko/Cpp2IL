import json
from pathlib import Path
import tempfile
import unittest

import owner_effect_array_element_store as oracle


class OwnerEffectArrayElementStoreOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                       "profile": oracle.PROFILE, "observations": oracle.observations()}

    def check(self, accepts=False):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(self.report), encoding="utf-8")
            if accepts:
                result = oracle.verify(path, "player", "2021.3.35f1")
                self.assertEqual((result["methods"], result["observations"]), (3, 37))
            else:
                with self.assertRaises(ValueError): oracle.verify(path, "player", "2021.3.35f1")

    def row(self, kind, active=True):
        return next(row for row in self.report["observations"] if row["kind"] == kind and row["before"]["first"]["active"] is active)

    def test_exact_scope_is_accepted(self):
        self.check(True)

    def test_owner_effect_survives_null_array_and_element_failures(self):
        for kind in ("array-null", "element-null", "reuse-null-array", "reuse-null-element"):
            with self.subTest(kind=kind):
                self.setUp()
                self.row(kind)["after"]["first"]["active"] = True
                self.check()

    def test_owner_effect_precedes_negative_and_empty_bounds_failures(self):
        for kind in ("element-null-negative", "array-empty", "reuse-negative-bound"):
            with self.subTest(kind=kind):
                self.setUp()
                self.row(kind)["after"]["first"]["active"] = True
                self.check()

    def test_null_owner_cannot_change_either_catalog(self):
        self.row("owner-null")["after"]["first"]["active"] = False
        self.check()

    def test_null_array_precedes_unsigned_negative_bounds(self):
        self.row("array-null-minimum")["exception"] = oracle.BOUNDS_FAILURE
        self.check()

    def test_boolean_store_preserves_byte_and_int32_neighbors(self):
        for owner, field in (("first", "before"), ("firstCell", "after"), ("firstCell", "before")):
            with self.subTest(owner=owner, field=field):
                self.setUp()
                self.row("aliased")["after"][owner][field] = 0
                self.check()

    def test_array_alias_identity_and_replacement_are_preserved(self):
        self.row("aliased")["after"]["firstArray"][1] = "second"
        self.check()
        self.setUp()
        self.row("reuse-replaced-array")["after"]["firstCell"]["enabled"] = False
        self.check()

    def test_shared_array_does_not_redirect_the_owner_effect(self):
        self.row("shared-other-owner")["after"]["first"]["active"] = False
        self.check()

    def test_integer_and_boolean_values_keep_distinct_types(self):
        self.row("aliased")["after"]["firstCell"]["enabled"] = 0
        self.check()

    def test_missing_or_reordered_observations_do_not_reduce_scope(self):
        self.report["observations"].pop()
        self.check()
        self.setUp()
        rows = self.report["observations"]
        rows[-1], rows[-2] = rows[-2], rows[-1]
        self.check()

    def test_wrong_platform_and_missing_partial_effects_are_rejected(self):
        self.report["platform"] = "WindowsEditor"
        self.check()
        self.setUp()
        self.row("element-null")["after"] = self.row("element-null")["before"]
        self.check()


if __name__ == "__main__":
    unittest.main()
