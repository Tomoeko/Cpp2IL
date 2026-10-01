import json
from pathlib import Path
import tempfile
import unittest

import framework_ancestor_array_argument as oracle


class FrameworkAncestorArrayArgumentOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                       "profile": oracle.PROFILE, "observations": oracle.observations()}

    def check(self, accepts=False):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(self.report), encoding="utf-8")
            if accepts:
                result = oracle.verify(path, "player", "2021.3.35f1")
                self.assertEqual((result["methods"], result["observations"]), (4, 30))
            else:
                with self.assertRaises(ValueError): oracle.verify(path, "player", "2021.3.35f1")

    def row(self, kind):
        return next(row for row in self.report["observations"] if row["kind"] == kind)

    def test_exact_scope_is_accepted(self):
        self.check(True)

    def test_framework_ancestor_identity_cannot_be_replaced_by_object(self):
        self.row("declarations")["baseTypes"]["Cell"] = "System.Object"
        self.check()

    def test_bounds_failure_must_precede_callee_effects(self):
        self.row("array-empty")["after"]["first"]["calls"] += 1
        self.check()

    def test_null_receiver_and_null_array_must_preserve_both_cells(self):
        for kind in ("cell-null", "array-null", "reuse-cleared-array"):
            with self.subTest(kind=kind):
                self.setUp()
                self.row(kind)["after"]["second"]["value"] = "null"
                self.check()

    def test_a_null_element_is_a_valid_argument(self):
        self.row("first-null")["exception"] = oracle.NULL_FAILURE
        self.check()

    def test_element_and_array_replacement_must_be_observed(self):
        for kind in ("first-second", "reuse-replaced-element", "reuse-replaced-array"):
            with self.subTest(kind=kind):
                self.setUp()
                after = self.row(kind)["after"]["first"]
                after["value"] = "first" if after["value"] != "first" else "second"
                self.check()

    def test_sharing_an_array_does_not_share_counter_effects(self):
        self.row("shared-array-second-cell")["after"]["first"]["calls"] += 1
        self.check()

    def test_overflow_requires_signed_int32_and_typed_values(self):
        self.row("counter-overflow")["after"]["first"]["calls"] = 1 << 31
        self.check()
        self.setUp()
        self.row("defaults")["calls"] = False
        self.check()

    def test_missing_or_reordered_rows_cannot_reduce_the_scope(self):
        self.report["observations"].pop()
        self.check()
        self.setUp()
        rows = self.report["observations"]
        rows[-1], rows[-2] = rows[-2], rows[-1]
        self.check()

    def test_wrong_stage_and_exception_type_are_rejected(self):
        self.report["platform"] = "WindowsEditor"
        self.check()
        self.setUp()
        self.row("array-empty")["exception"] = oracle.NULL_FAILURE
        self.check()


if __name__ == "__main__":
    unittest.main()
