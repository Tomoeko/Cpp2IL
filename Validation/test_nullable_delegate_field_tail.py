import json
import tempfile
import unittest
from pathlib import Path

from nullable_delegate_field_tail import observations, verify


class NullableDelegateFieldTailOracleTests(unittest.TestCase):
    def check(self, rows, **changes):
        report = {
            "unityVersion": "2021.3.35f1", "stage": "player",
            "platform": "WindowsPlayer", "profile": "nullable-delegate-field-tail",
            "observations": rows, **changes,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_complete_oracle(self):
        rows = observations()
        self.assertEqual(len(rows), 13)
        self.assertEqual(self.check(rows)["methods"], 4)

    def test_constructor_defaults_are_observed_before_field_initialization(self):
        for key in ("directNeighbor", "paddedNeighbor", "paddingIntact",
                    "directCallbackNull", "paddedCallbackNull"):
            rows = observations()
            rows[0][key] = False
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_effect_order_and_exception_mutations(self):
        for index, key, value in (
            (1, "instanceCount", 1),
            (5, "trace", "IISSI"),
            (8, "staticCount", 2),
            (9, "exception", "none"),
            (11, "exception", "none"),
            (12, "paddingIntact", False),
        ):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_strict_types_order_and_header(self):
        rows = observations()
        rows[1]["instanceCount"] = True
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
