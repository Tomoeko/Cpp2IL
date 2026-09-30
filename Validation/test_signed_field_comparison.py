import copy
import json
import tempfile
import unittest
from pathlib import Path

import signed_field_comparison as oracle


class SignedFieldComparisonTests(unittest.TestCase):
    def verify(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "behavior.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer",
                                        "stage": "player", "profile": "signed-field-comparison", "observations": rows}))
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_signed_extremes_distinguish_ordering_from_wrapping_subtraction(self):
        rows = oracle.observations()
        self.assertEqual(len(rows), 164)
        self.assertEqual(self.verify(rows)["status"], "passed")
        distinct = [row for row in rows if row["kind"] == "distinct" and row["operation"] == "compare"]
        self.assertEqual([row["result"] for row in distinct[:4]], [-1, 1, 0, 0])
        changed = copy.deepcopy(rows)
        changed[2]["result"] = 1
        with self.assertRaises(ValueError):
            self.verify(changed)

    def test_alias_identity_repeated_state_and_padding_are_authenticated(self):
        rows = oracle.observations()
        alias = next(row for row in rows if row["kind"] == "alias" and row["operation"] == "comparePadded")
        self.assertEqual(alias["result"], 0)
        self.assertEqual(alias["firstAfter"], alias["secondAfter"])
        changed = copy.deepcopy(rows)
        target = next(row for row in changed if row["kind"] == "alias" and row["operation"] == "comparePadded")
        target["secondAfter"]["padding"][0] = 0
        with self.assertRaises(ValueError):
            self.verify(changed)

    def test_nulls_and_typed_results_do_not_pass_as_successful_comparisons(self):
        rows = oracle.observations()
        for kind in ("first-null", "second-null", "both-null", "comparer-null"):
            target = next(row for row in rows if row["kind"] == kind)
            self.assertIsNone(target["result"])
            self.assertEqual(target["exception"], "System.NullReferenceException")
        changed = copy.deepcopy(rows)
        target = next(row for row in changed if row["kind"] == "distinct" and row["firstKey"] == row["secondKey"])
        target["result"] = False
        with self.assertRaises(ValueError):
            self.verify(changed)


if __name__ == "__main__":
    unittest.main()
