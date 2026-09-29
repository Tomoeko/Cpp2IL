import json
import tempfile
import unittest
from pathlib import Path

from object_reference_store import observations, verify


class ObjectReferenceStoreOracleTests(unittest.TestCase):
    def check(self, rows, **changes):
        report = {
            "unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
            "profile": "object-reference-store", "observations": rows, **changes,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report, ensure_ascii=False), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_mixed_values_and_null_owners_are_counted(self):
        rows = observations()
        self.assertEqual(len(rows), 12)
        self.assertEqual(self.check(rows)["methods"], 2)
        self.assertEqual([row["kind"] for row in rows[-3:]],
                         ["null-owner-object", "null-owner-null", "null-owner-string"])
        self.assertEqual(rows[2]["itemAfter"], "string:text-雪")
        self.assertEqual(rows[3]["itemAfter"], "int:23")
        self.assertEqual(rows[8]["itemBefore"], rows[7]["itemAfter"])

    def test_identity_write_order_and_neighbors_are_checked(self):
        for index, key, value in ((7, "itemAfter", "int:18"), (8, "itemBefore", "string:seed"),
                                  (6, "itemSameBefore", False), (1, "itemSameValue", False),
                                  (2, "prefixSame", False), (4, "suffixSame", False),
                                  (3, "sentinelAfter", 62)):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_null_failure_and_types_are_checked(self):
        for index, key, value in ((9, "exception", "none"), (10, "exception", "none"),
                                  (11, "itemSameValue", True), (3, "itemAfter", "string:23"),
                                  (0, "sentinelBefore", 61.0), (2, "itemSameValue", 1)):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_missing_duplicate_and_wrong_profile_reject(self):
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check(rows)
        with self.assertRaises(ValueError):
            self.check(observations(), profile="reference-store")
        with self.assertRaises(ValueError):
            self.check(observations(), platform="OSXEditor")


if __name__ == "__main__":
    unittest.main()
