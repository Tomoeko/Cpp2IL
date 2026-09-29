import json
import tempfile
import unittest
from pathlib import Path

from side_effect_class_cctor import observations, verify


class SideEffectClassConstructorOracleTests(unittest.TestCase):
    def check(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps({
                "unityVersion": "2021.3.35f1",
                "platform": "WindowsPlayer", "stage": "player",
                "profile": "side-effect-class-cctor", "observations": rows,
            }), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_initialization_effects_and_once_only_behavior(self):
        rows = observations()
        self.assertEqual(self.check(rows)["observations"], 3)
        for index, key, replacement in (
            (0, "events", 1), (1, "events", 0),
            (1, "marker", 0), (1, "bias", "00000000"),
            (2, "events", 2), (2, "marker", 37),
        ):
            changed = observations()
            changed[index][key] = replacement
            with self.assertRaises(ValueError):
                self.check(changed)


if __name__ == "__main__":
    unittest.main()
