import json
from pathlib import Path
import tempfile
import unittest

import native_direct_generic_reference_invocation as oracle


class DirectGenericReferenceInvocationOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                       "profile": oracle.PROFILE, "observations": oracle.observations()}

    def row(self, kind):
        return next(row for row in self.report["observations"] if row["kind"] == kind)

    def check(self, accepts=False):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(self.report), encoding="utf-8")
            if accepts:
                result = oracle.verify(path, "player", "2021.3.35f1")
                self.assertEqual((result["methods"], result["observations"]), (6, 40))
            else:
                with self.assertRaises(ValueError): oracle.verify(path, "player", "2021.3.35f1")

    def test_full_original_generic_scope_is_accepted(self):
        self.check(True)

    def test_generic_definition_and_reference_constraint_cannot_be_omitted(self):
        self.row("declarations")["methods"] = 5
        self.check()
        self.setUp()
        self.row("declarations")["genericParameters"]["Relay.Echo"][0]["attributes"] = "None"
        self.check()

    def test_both_closed_types_keep_argument_and_result_identity(self):
        for kind in ("echo-first-value-a", "echo-second-value-b", "wrapper-first-value-b", "wrapper-second-value-a"):
            with self.subTest(kind=kind):
                self.setUp()
                self.row(kind)["result"] = "other"
                self.check()

    def test_a_null_argument_returns_null_after_the_counter_effect(self):
        self.row("wrapper-first-value-null")["exception"] = oracle.NULL_FAILURE
        self.check()
        self.setUp()
        self.row("echo-second-value-null")["after"]["shared"] = 0
        self.check()

    def test_a_null_receiver_cannot_run_the_generic_body(self):
        self.row("echo-first-receiver-null")["after"]["shared"] += 1
        self.check()
        self.setUp()
        self.row("wrapper-second-receiver-null")["result"] = "second-a"
        self.check()

    def test_cross_wrapper_overflow_keeps_signed_int32_effects(self):
        self.row("cross-wrapper-overflow-second")["after"]["overflow"] = 1 << 31
        self.check()
        self.setUp()
        self.row("defaults")["calls"] = False
        self.check()

    def test_shared_arguments_do_not_share_receiver_counters(self):
        self.row("independent-first-shared-input")["after"]["shared"] += 1
        self.check()

    def test_repeat_and_direct_calls_cannot_be_replaced_by_pure_identity(self):
        for kind in ("repeat-first-a", "direct-after-wrappers-second"):
            with self.subTest(kind=kind):
                self.setUp()
                row = self.row(kind)
                row["after"] = row["before"].copy()
                self.check()

    def test_missing_or_reordered_rows_do_not_reduce_the_scope(self):
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
        self.row("wrapper-first-receiver-null")["exception"] = "System.ArgumentNullException"
        self.check()


if __name__ == "__main__":
    unittest.main()
