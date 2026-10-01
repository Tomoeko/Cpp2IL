import json
from pathlib import Path
import tempfile
import unittest

import native_nested_scalar_parameter_store as oracle


class NestedScalarParameterStoreOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                       "profile": oracle.PROFILE, "observations": oracle.observations()}

    def check(self, accepts=False):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(self.report), encoding="utf-8")
            if accepts:
                result = oracle.verify(path, "player", "2021.3.35f1")
                self.assertEqual((result["methods"], result["observations"]), (6, 98))
            else:
                with self.assertRaises(ValueError): oracle.verify(path, "player", "2021.3.35f1")

    def row(self, kind):
        return next(row for row in self.report["observations"] if row["kind"] == kind)

    def test_full_scope_is_accepted(self):
        self.check(True)

    def test_null_failure_cannot_write_or_change_the_other_cell(self):
        for kind in ("int32-null-target-0", "single-null-owner-9", "reuse-null-target"):
            with self.subTest(kind=kind):
                self.setUp()
                self.row(kind)["after"]["second"]["signed"] += 1
                self.check()

    def test_store_cannot_change_neighbor_fields(self):
        self.row("boolean-value-1")["after"]["first"]["before"] += 1
        self.check()

    def test_uint32_must_not_be_reinterpreted_as_signed(self):
        self.row("uint32-value-6")["after"]["first"]["unsigned"] = -1
        self.check()

    def test_single_preserves_sign_and_nan_payloads(self):
        for kind in ("single-value-1", "single-value-9", "single-value-11"):
            with self.subTest(kind=kind):
                self.setUp()
                self.row(kind)["after"]["first"]["amountBits"] ^= 1
                self.check()

    def test_retargeted_and_shared_cells_must_receive_the_effect(self):
        for kind in ("retarget-first-to-second", "shared-target-single", "retarget-first-back"):
            with self.subTest(kind=kind):
                self.setUp()
                row = self.row(kind)
                row["after"]["first"], row["after"]["second"] = row["after"]["second"], row["after"]["first"]
                self.check()

    def test_typed_values_full_denominator_and_row_order_are_required(self):
        self.row("defaults")["cell"]["signed"] = False
        self.check()
        self.setUp()
        self.report["observations"].pop()
        self.check()
        self.setUp()
        rows = self.report["observations"]
        rows[-1], rows[-2] = rows[-2], rows[-1]
        self.check()

    def test_editor_transport_quiets_only_signaling_nan_and_retains_raw_input(self):
        rows = oracle.observations("editor")
        row = next(row for row in rows if row["kind"] == "single-value-11")
        self.assertEqual(row["incoming"], 0x7f800002)
        self.assertEqual(row["transportedIncomingBits"], 0x7fc00002)
        self.assertEqual(row["after"]["first"]["amountBits"], 0x7fc00002)
        for kind, bits in (("single-value-1", 0x80000000), ("single-value-2", 1), ("single-value-9", 0x7fc00001)):
            other = next(row for row in rows if row["kind"] == kind)
            self.assertEqual(other["transportedIncomingBits"], bits)
        self.assertEqual(len(rows), 98)

    def test_native_transport_cannot_quiet_a_signaling_nan(self):
        row = self.row("single-value-11")
        row["transportedIncomingBits"] = 0x7fc00002
        row["after"]["first"]["amountBits"] = 0x7fc00002
        self.check()


if __name__ == "__main__":
    unittest.main()
