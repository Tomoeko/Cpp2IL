import json
import tempfile
import unittest
from pathlib import Path

from wide_reference_null import observations, verify


class WideReferenceNullOracleTests(unittest.TestCase):
    def check(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps({
                "unityVersion": "2021.3.35f1", "stage": "player",
                "platform": "WindowsPlayer", "profile": "wide-reference-null",
                "observations": rows,
            }), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_states_alias_and_receiver_exception(self):
        rows = observations()
        self.assertEqual(len(rows), 8)
        self.assertEqual(self.check(rows)["methods"], 4)
        self.assertEqual(rows[1]["hasNear"], True)
        self.assertEqual(rows[2]["hasFirst"], True)
        self.assertEqual(rows[3]["isSecondNull"], False)
        self.assertEqual(rows[5]["allTargetsAlias"], True)
        self.assertEqual(rows[7]["firstFailure"], "System.NullReferenceException")

    def test_width_field_and_exception_mutations_reject(self):
        for index, key, value in (
                (1, "hasNear", 1),
                (2, "hasFirstRepeat", False),
                (3, "isSecondNull", True),
                (4, "pad06Same", False),
                (5, "allTargetsAlias", False),
                (7, "firstFailure", "none"),
                (7, "secondFailure", "System.IndexOutOfRangeException"),
        ):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_denominator_and_duplicate_key_reject(self):
        with self.assertRaises(ValueError):
            self.check(observations()[:-1])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text('{"unityVersion":"2021.3.35f1",'
                            '"platform":"WindowsPlayer","stage":"player",'
                            '"profile":"wide-reference-null",'
                            '"profile":"wide-reference-null","observations":[]}',
                            encoding="utf-8")
            with self.assertRaises(ValueError):
                verify(path, "player", "2021.3.35f1")


if __name__ == "__main__":
    unittest.main()
