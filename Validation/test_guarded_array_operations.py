import copy
import json
import tempfile
import unittest
from pathlib import Path

import guarded_array_operations as oracle


class GuardedArrayOperationsOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": "guarded-array-operations", "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_complete_typed_denominator(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (9, 38))

    def test_wrong_types_effects_exception_order_and_aliases_reject(self):
        mutations = (
            ("declarations", "owner", 1),
            ("twice-3", "counter", 11),
            ("twice-2", "result", 4294967294),
            ("set-write-failure", "observed", 17),
            ("set-read-failure", "observed", 4),
            ("sum-left-bounds-before-null-right", "exception", "System.NullReferenceException"),
            ("node-alias", "afterValues", [11, None, -9]),
            ("node-null-element", "witnessCalls", 5),
            ("node-null-holder", "counter", 10),
        )
        for kind, field, value in mutations:
            with self.subTest(kind=kind, field=field):
                changed = copy.deepcopy(self.report)
                row = next(item for item in changed["observations"] if item["kind"] == kind)
                row[field] = value
                with self.assertRaises(ValueError):
                    self.verify(changed)

    def test_removed_or_reordered_observations_reject(self):
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            changed = dict(self.report, observations=rows)
            with self.assertRaises(ValueError):
                self.verify(changed)


if __name__ == "__main__":
    unittest.main()
