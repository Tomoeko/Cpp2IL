import json
import tempfile
import unittest
from pathlib import Path

from nested_byte_field_read import observations, verify


class NestedByteFieldReadOracleTests(unittest.TestCase):
    def check(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps({
                "unityVersion": "2021.3.35f1", "stage": "player",
                "platform": "WindowsPlayer", "profile": "nested-byte-field-read",
                "observations": rows,
            }), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_covers_defaults_nulls_full_width_and_aliases(self):
        rows = observations()
        self.assertEqual(len(rows), 260)
        self.assertEqual(self.check(rows)["methods"], 3)
        self.assertEqual([row["input"] for row in rows[4:]], list(range(256)))
        self.assertEqual(rows[131]["first"], 127)
        self.assertEqual(rows[132]["first"], 128)
        self.assertEqual(rows[-1]["aliasResult"], 255)

    def test_rejects_constructor_null_width_and_side_effect_mutations(self):
        for row, key, changed in (
            (0, "value", 1),
            (1, "childNull", False),
            (2, "failure", "none"),
            (3, "referenceSame", False),
            (131, "first", -1),
            (132, "first", -128),
            (132, "second", 0),
            (259, "aliasResult", 0),
            (259, "cellValue", 0),
            (259, "ownerReferenceSame", False),
            (259, "cellNeighbor", 0),
            (259, "first", True),
        ):
            rows = observations()
            rows[row][key] = changed
            with self.assertRaises(ValueError):
                self.check(rows)
        with self.assertRaises(ValueError):
            self.check(observations()[:-1])


if __name__ == "__main__":
    unittest.main()
