import json
import tempfile
import unittest
from pathlib import Path

from layered_virtual_tail_dispatch import observations, verify


class LayeredVirtualTailDispatchOracleTests(unittest.TestCase):
    def check(self, rows, **changes):
        report = {
            "unityVersion": "2021.3.35f1", "stage": "player",
            "platform": "WindowsPlayer", "profile": "layered-virtual-tail-dispatch",
            "observations": rows, **changes,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_complete_oracle(self):
        rows = observations()
        self.assertEqual(len(rows), 5)
        self.assertEqual(self.check(rows)["methods"], 8)

    def test_dispatch_and_neighbors_reject_mutations(self):
        for index, key, value in (
            (0, "baseMarker", 3), (1, "derivedMarker", 11),
            (2, "baseTag", 7), (3, "derivedNeighbor", 0),
            (4, "exception", "none"),
        ):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_strict_types_order_and_report_header(self):
        rows = observations()
        rows[0]["baseMarker"] = True
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
