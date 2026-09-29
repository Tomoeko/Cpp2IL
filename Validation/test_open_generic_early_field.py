import json
import tempfile
import unittest
from pathlib import Path

from open_generic_early_field import PAYLOADS, VALUES, observations, verify


class OpenGenericEarlyFieldOracleTests(unittest.TestCase):
    def check(self, rows, **changes):
        report = {"unityVersion": "2021.3.35f1", "stage": "player",
                  "platform": "WindowsPlayer", "profile": "open-generic-early-field",
                  "observations": rows, **changes}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_all_methods_and_rows_are_counted(self):
        rows = observations()
        self.assertEqual(len(rows), len(PAYLOADS) * (1 + len(VALUES) * 2) + 3)
        self.assertEqual(self.check(rows)["methods"], 4)

    def test_early_field_later_payload_and_null_failure_mutations_reject(self):
        for index, key, value in ((1, "firstResult", 0), (1, "anchorSame", False),
                                  (1, "flagResult", True),
                                  (1, "payloadAfter", "changed"),
                                  (0, "payloadDefault", False), (-1, "failure", "none")):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_numeric_types_and_denominator_reject(self):
        rows = observations()
        rows[1]["firstResult"] = float(rows[1]["firstResult"])
        with self.assertRaises(ValueError):
            self.check(rows)
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check(rows)


if __name__ == "__main__":
    unittest.main()
