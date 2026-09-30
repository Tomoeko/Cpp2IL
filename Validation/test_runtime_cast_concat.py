import copy
import json
from pathlib import Path
import tempfile
import unittest

from runtime_cast_concat import observations, verify


class RuntimeCastConcatOracleTests(unittest.TestCase):
    def check_report(self, rows):
        report = {"unityVersion": "2021.3.35f1", "stage": "player",
                  "platform": "WindowsPlayer", "profile": "runtime-cast-concat",
                  "observations": rows}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_type_identity_and_repeated_type_are_required_with_boolean_values(self):
        self.assertEqual(self.check_report(observations())["observations"], 36)
        for field, replacement in (("name", "RuntimeCastConcatFixture.BaseNode"),
                                   ("matchesManagedType", False),
                                   ("sameRepeatedType", False),
                                   ("matchesManagedType", 1),
                                   ("sameRepeatedType", 1)):
            rows = copy.deepcopy(observations())
            next(row for row in rows if row["kind"] == "target-type")[field] = replacement
            with self.subTest(field=field, replacement=replacement):
                with self.assertRaises(ValueError):
                    self.check_report(rows)

    def test_duplicate_type_identity_key_is_rejected(self):
        report = {"unityVersion": "2021.3.35f1", "stage": "player",
                  "platform": "WindowsPlayer", "profile": "runtime-cast-concat",
                  "observations": observations()}
        text = json.dumps(report).replace('"matchesManagedType": true',
                                         '"matchesManagedType": false, "matchesManagedType": true')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(text, encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "duplicate JSON keys"):
                verify(path, "player", "2021.3.35f1")


if __name__ == "__main__":
    unittest.main()
