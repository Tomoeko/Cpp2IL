import json
import tempfile
import unittest
from pathlib import Path

from empty_object_constructor import observations, verify


class EmptyObjectConstructorOracleTests(unittest.TestCase):
    def check(self, rows, **changes):
        report = {
            "unityVersion": "2021.3.35f1", "stage": "player",
            "platform": "WindowsPlayer", "profile": "empty-object-constructor",
            "observations": rows, **changes,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return verify(path, "player", "2021.3.35f1")

    def test_distinct_default_constructors_and_64bit_boundary(self):
        rows = observations()
        self.assertEqual(len(rows), 8)
        self.assertEqual(self.check(rows)["methods"], 2)
        self.assertEqual(rows[0]["number"], 0)
        self.assertEqual(rows[4]["sameFirst"], False)
        self.assertEqual(rows[6]["count"], (1 << 63) - 1)

    def test_identity_and_default_mutations_reject(self):
        for index, key, value in ((0, "sameFirst", False), (1, "sameSecond", False),
                                  (2, "payloadSame", False), (3, "number", 73),
                                  (4, "payloadNull", False), (5, "label", ""),
                                  (6, "count", -1), (7, "sameOther", True)):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)

    def test_strict_types_and_complete_report(self):
        for index, key, value in ((0, "number", False), (1, "flag", 0),
                                  (5, "count", 0.0)):
            rows = observations()
            rows[index][key] = value
            with self.assertRaises(ValueError):
                self.check(rows)
        for rows in (observations()[:-1], observations() + [observations()[0]]):
            with self.assertRaises(ValueError):
                self.check(rows)
        for change in ({"stage": "editor"}, {"platform": "OSXPlayer"},
                       {"profile": "shared-inert-constructor"}):
            with self.assertRaises(ValueError):
                self.check(observations(), **change)


if __name__ == "__main__":
    unittest.main()
