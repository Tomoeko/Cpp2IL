import copy
import json
import tempfile
import unittest
from pathlib import Path

import typed_field_address_discarded_result as oracle


class TypedFieldAddressDiscardedResultOracleTests(unittest.TestCase):
    def test_callee_signed_results_storage_wrap_and_guard_are_distinct(self):
        rows = oracle.observations()
        calls = [row for row in rows if row["kind"] == "callee" and row["method"] == "IncrementAndReport"]
        self.assertEqual({row["result"] for row in calls}, set(oracle.MODES))
        boundary = next(row for row in calls if row["input"] == 9223372036854775807 and row["mode"] == -1)
        self.assertEqual((boundary["value"], boundary["copyValue"], boundary["result"]),
                         (-9223372036854775808, 9223372036854775807, -1))
        disabled = [row for row in rows if row["kind"] == "operation"
                    and row["method"] == "GuardedApplyDefault" and not row["enabled"]]
        self.assertEqual(len(disabled), 12)
        self.assertTrue(all(row["first"] == row["input"] for row in disabled))
        aliases = [row for row in rows if row["kind"] == "ref-alias"]
        self.assertEqual([row["first"] for row in aliases], [-9223372036854775808, 0])
        self.assertTrue(all(row["slotAfter"] == row["first"] and row["copyFirst"] == 19 for row in aliases))

    def test_exact_oracle_rejects_enum_result_and_effect_loss(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            for defect in ("enum", "callee", "second-field", "guard", "alias", "null"):
                rows = copy.deepcopy(oracle.observations())
                if defect == "enum":
                    next(row for row in rows if row["kind"] == "callee" and row["mode"] == -1)["result"] = 4294967295
                elif defect == "callee":
                    row = next(row for row in rows if row["kind"] == "callee" and row["method"] == "IncrementAndReport")
                    row["value"] = row["input"]
                elif defect == "second-field":
                    next(row for row in rows if row["kind"] == "operation" and row["method"] == "ResetBothDiscard")["second"] = -23
                elif defect == "guard":
                    row = next(row for row in rows if row["kind"] == "operation" and row["method"] == "GuardedApplyDefault"
                               and not row["enabled"])
                    row["first"] = oracle.wrap(row["first"] + 1, 64)
                elif defect == "alias":
                    next(row for row in rows if row["kind"] == "ref-alias")["copyFirst"] = 0
                else:
                    next(row for row in rows if row["kind"] == "null-owner")["exception"] = "none"
                path.write_text(json.dumps({"profile": "typed-field-address-discarded-result", "stage": "player",
                                           "unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "observations": rows}))
                with self.assertRaises(ValueError, msg=defect):
                    oracle.verify(path, "player", "2021.3.35f1")


if __name__ == "__main__":
    unittest.main()
