"""Protect the loop oracle's side-effect, overflow and zero-iteration boundaries."""

import json
from pathlib import Path
import tempfile
import unittest

import loop_calls


class LoopCallOracleTests(unittest.TestCase):
    def setUp(self):
        scratch = Path(__file__).resolve().parent.parent / "Files/validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "behavior.json"
        self.report = {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                       "profile": "loop-calls", "observations": loop_calls.observations()}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return loop_calls.verify(self.path, "player", "2021.3.35f1")

    def row(self, **fields):
        return next(row for row in self.report["observations"] if all(row.get(key) == value for key, value in fields.items()))

    def test_complete_denominator_and_missing_iteration_case(self):
        self.assertEqual(self.verify()["observations"], 983)
        self.report["observations"].pop(76)
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_constructor_preserves_both_default_field_values(self):
        row = self.row(kind="constructor")
        self.assertEqual((row["valueAfter"], row["callsAfter"]), (0, 0))
        for field in ("valueAfter", "callsAfter"):
            with self.subTest(field=field):
                row[field] = 1
                with self.assertRaisesRegex(ValueError, "independent oracle"):
                    self.verify()
                row[field] = 0

    def test_closed_form_known_values_and_wrapping(self):
        self.assertEqual(loop_calls.loop_result(3, 7, 4, 10), {"result": 32, "valueAfter": 9, "callsAfter": 11})
        self.assertEqual(loop_calls.loop_result(2147483647, 2147483647, 2, 1),
                         {"result": 0, "valueAfter": -2147483648, "callsAfter": -2147483647})

    def test_zero_delta_still_has_a_call_side_effect(self):
        row = self.row(kind="step", initialValue=0, initialCalls=0, delta=0)
        self.assertEqual(row["result"], 0)
        row["callsAfter"] = 0
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_zero_iterations_preserve_seed(self):
        row = self.row(kind="run", count=0, seed=19)
        self.assertEqual(row["result"], 19)
        row["result"] = 0
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_early_exit_includes_the_matching_calls_effects(self):
        row = self.row(kind="until", initialValue=3, initialCalls=0, count=7, stop=3)
        self.assertEqual((row["result"], row["valueAfter"], row["callsAfter"]), (3, 3, 1))
        row["callsAfter"] = 0
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_eager_null_check_is_not_allowed_when_loop_does_not_execute(self):
        row = self.row(kind="null", member="run", argument=-3)
        self.assertEqual((row["result"], row["exception"]), (19, "none"))
        row.update(result=None, exception="System.NullReferenceException")
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()

    def test_actual_call_path_must_throw_for_null_receiver(self):
        row = self.row(kind="null", member="until", argument=1)
        row.update(result=0, exception="none")
        with self.assertRaisesRegex(ValueError, "independent oracle"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
