import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import native_stack_aggregate_argument as oracle


class NativeStackAggregateArgumentOracleTests(unittest.TestCase):
    def verify_rows(self, rows, stage="player"):
        with TemporaryDirectory() as folder:
            path = Path(folder) / "observations.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1",
                                       "platform": "WindowsEditor" if stage == "editor" else "WindowsPlayer",
                                       "stage": stage, "profile": "native-stack-aggregate-argument",
                                       "observations": rows}))
            return oracle.verify(path, stage, "2021.3.35f1")

    def test_editor_precision_cannot_replace_native_single_precision(self):
        for stage, other in (("editor", "player"), ("player", "editor")):
            with self.subTest(stage=stage):
                self.verify_rows(oracle.observations(stage), stage)
                with self.assertRaises(ValueError):
                    self.verify_rows(oracle.observations(other), stage)

    def test_byte_coverage_and_value_type_identity_are_required(self):
        for key, value in (("size", 8), ("forwardArgument", "System.Double"), ("methods", 1)):
            rows = oracle.observations()
            rows[0][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_rows(rows)
        rows = oracle.observations()
        rows[0]["fields"][-1]["offset"] = 4
        with self.assertRaises(ValueError):
            self.verify_rows(rows)

    def test_rounding_subnormals_zero_sign_and_nan_payload_are_observed(self):
        for case, value in ((1, "00000000"), (4, "3f800000"), (7, "00000000"), (11, "7fc00000")):
            rows = oracle.observations()
            next(row for row in rows if row.get("case") == case and row.get("operation") == 0)["result"] = value
            with self.subTest(case=case), self.assertRaises(ValueError):
                self.verify_rows(rows)

    def test_forward_call_result_and_incoming_value_are_separate_observations(self):
        rows = oracle.observations()
        next(row for row in rows if row.get("case") == 3 and row.get("operation") == 1)["result"] = "40c00000"
        with self.assertRaises(ValueError):
            self.verify_rows(rows)
        with self.assertRaises(ValueError):
            self.verify_rows(oracle.observations()[:-1])
