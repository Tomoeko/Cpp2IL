import json
import tempfile
import unittest
from pathlib import Path

from final_override_boolean_getter import observations, verify


class FinalOverrideBooleanGetterOracleTests(unittest.TestCase):
    def check(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps({
                "unityVersion": "2021.3.35f1", "stage": "player",
                "platform": "WindowsPlayer",
                "profile": "final-override-boolean-getter",
                "observations": rows,
            }), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_defaults_nulls_and_override_shadow_pairs(self):
        rows = observations()
        self.assertEqual(len(rows), 10)
        self.assertEqual(self.check(rows)["methods"], 6)
        self.assertEqual(rows[0]["baseReferenceNull"], True)
        self.assertEqual(rows[1]["referenceNull"], True)
        self.assertEqual(rows[2]["failure"], "System.NullReferenceException")
        self.assertEqual(rows[3]["failure"], "System.NullReferenceException")
        self.assertEqual([(row["inputFlag"], row["inputShadowFlag"])
                          for row in rows[6:]],
                         [(False, False), (False, True), (True, False), (True, True)])
        self.assertNotEqual(rows[7]["baseValue"], rows[7]["shadowValue"])
        self.assertNotEqual(rows[8]["baseValue"], rows[8]["shadowValue"])

    def test_rejects_slot_shadow_null_identity_and_type_mutations(self):
        for row, key, changed in (
            (0, "flag", True),
            (1, "shadowNeighbor", 1),
            (2, "failure", "none"),
            (3, "failure", "none"),
            (7, "baseValue", True),
            (7, "middleValue", True),
            (7, "shadowValue", False),
            (8, "repeatBaseValue", False),
            (8, "repeatShadowValue", True),
            (8, "baseAliasSame", False),
            (8, "baseReferenceSame", False),
            (8, "neighbor", 0),
            (8, "flagAfter", 1),
        ):
            rows = observations()
            rows[row][key] = changed
            with self.assertRaises(ValueError):
                self.check(rows)
        with self.assertRaises(ValueError):
            self.check(observations()[:-1])


if __name__ == "__main__":
    unittest.main()
