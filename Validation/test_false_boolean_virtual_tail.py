import json
import tempfile
import unittest
from pathlib import Path

from false_boolean_virtual_tail import observations, verify


class FalseBooleanVirtualTailOracleTests(unittest.TestCase):
    def check(self, rows, **changes):
        report = {
            "unityVersion": "2021.3.35f1", "stage": "player",
            "platform": "WindowsPlayer", "profile": "false-boolean-virtual-tail",
            "observations": rows, **changes,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_complete_oracle(self):
        rows = observations()
        self.assertEqual(len(rows), 8)
        self.assertEqual(self.check(rows)["methods"], 3)

    def test_dispatch_argument_order_and_null_failure(self):
        for index, key, value in (
            (2, "overrideMarker", 11),
            (2, "overrideLast", True),
            (3, "events", "[][T][F]"),
            (4, "overrideCalls", 2),
            (6, "events", "[][F][T][F][F][]"),
            (7, "exception", "none"),
            (7, "overrideCalls", 5),
        ):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_defaults_neighbors_and_strict_schema(self):
        for index, key, value in (
            (0, "baseMarker", 1),
            (0, "overrideNeighbor", 1),
            (5, "baseNeighbor", 0),
            (6, "overrideNeighbor", 0),
            (7, "overrideLast", 0),
        ):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)
        with self.assertRaises(ValueError):
            self.check(list(reversed(observations())))
        with self.assertRaises(ValueError):
            self.check(observations()[:-1])
        with self.assertRaises(ValueError):
            self.check(observations(), platform="OSXPlayer")


if __name__ == "__main__":
    unittest.main()
