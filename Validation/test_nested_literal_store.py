import json
from pathlib import Path
import tempfile
import unittest

import nested_literal_store


class NestedLiteralStoreOracleTests(unittest.TestCase):
    def report(self):
        return {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                "profile": "nested-literal-store", "observations": nested_literal_store.expected_observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return nested_literal_store.verify(path, "player", "2021.3.35f1")

    def test_complete_denominator(self):
        self.assertEqual(self.verify(self.report())["observations"], 720)

    def test_later_receiver_failure_keeps_the_first_store(self):
        report = self.report()
        row = next(row for row in report["observations"]
                   if row["operation"] == 1 and row["configuration"] == 2)
        self.assertEqual(row["firstAfter"]["signed"], -1)
        row["firstAfter"]["signed"] = row["seed"]
        with self.assertRaises(ValueError):
            self.verify(report)

    def test_replacement_does_not_replace_an_earlier_capture(self):
        report = self.report()
        row = next(row for row in report["observations"]
                   if row["operation"] == 3 and row["configuration"] == 0 and not row["enabled"])
        self.assertEqual(row["firstBinding"], "replacement")
        self.assertTrue(row["firstAfter"]["enabled"])
        row["firstAfter"]["enabled"] = False
        row["replacementAfter"]["enabled"] = True
        with self.assertRaises(ValueError):
            self.verify(report)

    def test_first_null_keeps_the_intervening_call_effect(self):
        report = self.report()
        row = next(row for row in report["observations"]
                   if row["operation"] == 1 and row["configuration"] == 1)
        self.assertNotEqual(row["marker"], row["seed"])
        row["marker"] = row["seed"]
        with self.assertRaises(ValueError):
            self.verify(report)

    def test_aliases_preserve_store_order_and_full_unsigned_bits(self):
        report = self.report()
        row = next(row for row in report["observations"]
                   if row["operation"] == 2 and row["configuration"] == 4)
        self.assertEqual(row["firstAfter"]["unsigned"], 4294967295)
        row["firstAfter"]["unsigned"] = 2147483648
        with self.assertRaises(ValueError):
            self.verify(report)

    def test_incomplete_or_wrong_target_results_are_rejected(self):
        for key, value in (("platform", "OSXPlayer"), ("unityVersion", "2021.3.34f1"),
                           ("observations", [])):
            with self.subTest(key=key):
                report = self.report()
                report[key] = value
                with self.assertRaises(ValueError):
                    self.verify(report)


if __name__ == "__main__":
    unittest.main()
