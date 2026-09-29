import json
import tempfile
import unittest
from pathlib import Path

from cctor_boolean_getter import observations, verify


class CctorBooleanGetterOracleTests(unittest.TestCase):
    def check(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps({
                "unityVersion": "2021.3.35f1",
                "platform": "WindowsPlayer", "stage": "player",
                "profile": "cctor-boolean-getter", "observations": rows,
            }), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_state_and_receiver_checks(self):
        rows = observations()
        self.assertEqual(self.check(rows)["observations"], 6)
        for index, key, replacement in (
            (0, "counter", 1), (1, "neighborAfter", True),
            (2, "first", True), (4, "repeat", False),
            (5, "failure", "none"),
        ):
            changed = observations()
            changed[index][key] = replacement
            with self.assertRaises(ValueError):
                self.check(changed)
        with self.assertRaises(ValueError):
            self.check(rows[:-1])


if __name__ == "__main__":
    unittest.main()
