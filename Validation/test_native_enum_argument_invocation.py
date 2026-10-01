import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import native_enum_argument_invocation as oracle


class NativeEnumArgumentInvocationOracleTests(unittest.TestCase):
    def verify_rows(self, rows):
        with TemporaryDirectory() as folder:
            path = Path(folder) / "observations.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer",
                                       "stage": "player", "profile": "native-enum-argument-invocation",
                                       "observations": rows}))
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_enum_identity_signedness_and_literals_are_required(self):
        for key, value in (("acceptParameter", "System.Int32"), ("underlying", "System.UInt32"), ("methods", 7)):
            rows = oracle.observations()
            rows[0][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_rows(rows)
        rows = oracle.observations()
        rows[0]["constants"][-1]["value"] = 4294967289
        with self.assertRaises(ValueError):
            self.verify_rows(rows)

    def test_effect_before_null_failure_cannot_be_erased(self):
        rows = oracle.observations()
        row = next(row for row in rows if row.get("operation") == 5 and row.get("case") == "second-null")
        row["first"]["calls"] = 10
        with self.assertRaises(ValueError):
            self.verify_rows(rows)

    def test_unnamed_values_and_receiver_aliasing_are_observed(self):
        for operation, case, replacement in ((3, "zero", 17), (4, "minimum", 0), (5, "alias", -7)):
            rows = oracle.observations()
            row = next(row for row in rows if row.get("operation") == operation and row.get("case") == case)
            row["first"]["last"] = replacement
            with self.subTest(operation=operation), self.assertRaises(ValueError):
                self.verify_rows(rows)
