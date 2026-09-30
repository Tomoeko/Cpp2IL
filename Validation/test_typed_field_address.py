import copy
import json
import tempfile
import unittest
from pathlib import Path

import typed_field_address as oracle


class TypedFieldAddressOracleTests(unittest.TestCase):
    def test_alias_and_guard_observations_distinguish_storage_from_copies(self):
        rows = oracle.observations()
        alias = [row for row in rows if row["kind"] == "ref-alias"]
        self.assertEqual([row["first"] for row in alias], [39, 31])
        self.assertTrue(all(row["slotAfter"] == row["first"] and row["copyFirst"] == 19 for row in alias))
        false_guard = [row for row in rows if row.get("method") == "GuardedAdvance"
                       and row["kind"] == "operation" and not row["enabled"]]
        self.assertEqual(len(false_guard), 14)
        self.assertTrue(all(row["result"] == 0 and row["first"] == 19 for row in false_guard))
        self.assertEqual(oracle.wrap(9223372036854775807 + 19, 64), -9223372036854775790)
        self.assertEqual(oracle.wrap(2147483647 + 31, 32), -2147483618)

    def test_oracle_rejects_copy_write_loss_and_neighbor_or_null_mutations(self):
        original = oracle.observations()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            for defect in ("copy", "neighbor", "null"):
                rows = copy.deepcopy(original)
                if defect == "copy":
                    row = next(row for row in rows if row["kind"] == "ref-alias")
                    row["first"] = row["assigned"]
                    row["slotAfter"] = row["assigned"]
                elif defect == "neighbor":
                    row = next(row for row in rows if row["kind"] == "operation")
                    row["neighbor"]["first"] += 1
                else:
                    next(row for row in rows if row["kind"] == "null-owner")["exception"] = "none"
                path.write_text(json.dumps({"profile": "typed-field-address", "stage": "player",
                                           "unityVersion": "2021.3.35f1", "platform": "WindowsPlayer",
                                           "observations": rows}))
                with self.assertRaises(ValueError, msg=defect):
                    oracle.verify(path, "player", "2021.3.35f1")


if __name__ == "__main__":
    unittest.main()
