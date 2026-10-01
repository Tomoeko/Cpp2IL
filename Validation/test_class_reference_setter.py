import copy
import json
import tempfile
import unittest
from pathlib import Path

from class_reference_setter import observations, verify


class ClassReferenceSetterOracleTests(unittest.TestCase):
    def verify_rows(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "behavior.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "stage": "player",
                                        "platform": "WindowsPlayer", "profile": "class-reference-setter",
                                        "observations": rows}))
            return verify(path, "player", "2021.3.35f1")

    def test_null_assignment_and_null_owner_remain_distinct(self):
        rows = observations()
        self.assertEqual(len(rows), 31)
        self.assertEqual(self.verify_rows(rows)["methods"], 4)
        next(row for row in rows if row.get("check") == "null-owner-null")["exception"] = "none"
        with self.assertRaises(ValueError):
            self.verify_rows(rows)

    def test_lost_payload_identity_or_marker_is_rejected(self):
        for check in ("alias-replacement", "repeat-identity", "clear-second-unchanged",
                      "payload-neighbors-unchanged", "collection-retains-identity"):
            rows = copy.deepcopy(observations())
            next(row for row in rows if row.get("check") == check)["result"] = False
            with self.subTest(check=check), self.assertRaises(ValueError):
                self.verify_rows(rows)

    def test_collected_payload_is_not_accepted_as_a_store(self):
        rows = copy.deepcopy(observations())
        next(row for row in rows if row.get("check") == "collection-retains-payload")["result"] = False
        with self.assertRaises(ValueError):
            self.verify_rows(rows)


if __name__ == "__main__":
    unittest.main()
