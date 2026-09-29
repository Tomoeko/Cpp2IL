import json
import tempfile
import unittest
from pathlib import Path

from string_reference_store import observations, verify


class StringReferenceStoreOracleTests(unittest.TestCase):
    def check(self, rows, **changes):
        report = {
            "unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
            "profile": "string-reference-store", "observations": rows, **changes,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report, ensure_ascii=False), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_all_receiver_and_value_cases_are_counted(self):
        rows = observations()
        self.assertEqual(len(rows), 12)
        self.assertEqual(self.check(rows)["methods"], 2)
        self.assertEqual([row["kind"] for row in rows[-3:]],
                         ["null-owner-value", "null-owner-null", "null-owner-empty"])
        self.assertEqual(rows[5]["textAfter"], "café-雪")
        self.assertEqual(rows[8]["textBefore"], rows[7]["textAfter"])

    def test_write_order_identity_and_neighbors_are_checked(self):
        for index, key, value in ((7, "textAfter", "ssssss"), (8, "textBefore", "seed"),
                                  (6, "textSameBefore", False), (1, "textSameValue", False),
                                  (5, "prefixSame", False), (4, "suffixSame", False),
                                  (3, "sentinelAfter", 74)):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_null_failure_and_exact_types_are_checked(self):
        for index, key, value in ((9, "exception", "none"), (10, "exception", "none"),
                                  (11, "textSameValue", True), (5, "textAfter", "cafe-雪"),
                                  (0, "sentinelBefore", 73.0), (2, "textSameValue", 1)):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_missing_duplicate_or_wrong_profile_rejects(self):
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check(rows)
        with self.assertRaises(ValueError):
            self.check(observations(), profile="reference-store")
        with self.assertRaises(ValueError):
            self.check(observations(), platform="OSXEditor")


if __name__ == "__main__":
    unittest.main()
