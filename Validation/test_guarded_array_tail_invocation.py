import copy
import json
import tempfile
import unittest
from pathlib import Path

import guarded_array_tail_invocation as oracle


class GuardedArrayTailInvocationOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": "guarded-array-tail-invocation", "observations": oracle.observations()}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_complete_typed_denominator(self):
        result = self.verify(self.report)
        self.assertEqual((result["methods"], result["observations"]), (10, 58))

    def test_changed_call_effects_arguments_and_exception_order_reject(self):
        mutations = (("read-0", "firstCalls", 7), ("accept-0", "firstValue", 0),
                     ("marker-null-array", "marker", 5), ("produced-empty", "marker", 5),
                     ("read-3", "exception", "System.NullReferenceException"),
                     ("read-null-element", "secondCalls", 0), ("parameter-null-owner", "result", None),
                     ("accept-max", "thirdValue", -1), ("declarations", "owners", 1))
        for kind, field, value in mutations:
            with self.subTest(kind=kind, field=field):
                changed = copy.deepcopy(self.report)
                next(row for row in changed["observations"] if row["kind"] == kind)[field] = value
                with self.assertRaises(ValueError):
                    self.verify(changed)

    def test_missing_or_reordered_observations_reject(self):
        for rows in (self.report["observations"][:-1], list(reversed(self.report["observations"]))):
            with self.assertRaises(ValueError):
                self.verify(dict(self.report, observations=rows))


if __name__ == "__main__":
    unittest.main()
