import unittest

from guarded_array_length import expected_observations


class ArrayLengthOracleTests(unittest.TestCase):
    def row(self, operation, length, index=0, marker=-1):
        return next(row for row in expected_observations() if row["operation"] == operation and
                    row["length"] == length and row["index"] == index and row["marker"] == marker)

    def test_null_keeps_the_prior_effect_only(self):
        self.assertEqual(self.row("copy", -1)["markerAfter"], 0)
        self.assertEqual(self.row("copy", -1)["indexAfter"], 0)
        self.assertEqual(self.row("advance", -1)["indexAfter"], 1)
        self.assertEqual(self.row("advance", -1)["markerAfter"], -1)

    def test_empty_last_comparison_uses_negative_one(self):
        self.assertEqual(self.row("last", 0, -1)["indexAfter"], 0)
        self.assertEqual(self.row("last", 0, -2147483648)["indexAfter"], -2147483647)

    def test_counter_overflow_preserves_signed_comparison(self):
        self.assertEqual(self.row("advance", 3, 2147483647)["indexAfter"], -2147483648)
        self.assertEqual(self.row("last", 3, 2147483647)["indexAfter"], -2147483648)

    def test_marker_wraps_and_parameter_length_stays_separate(self):
        self.assertEqual(self.row("parameter", 7, 3, 2147483647)["markerAfter"], -2147483648)
        self.assertEqual(self.row("parameter", 7, 3)["result"], 7)
        self.assertEqual(self.row("parameter", 7, 3)["indexAfter"], 3)

    def test_fixed_denominator_and_object_array_projection(self):
        self.assertEqual(len(expected_observations()), 250)
        a, b = self.row("parameter", 3), self.row("objects", 3)
        self.assertEqual({key: value for key, value in a.items() if key != "operation"},
                         {key: value for key, value in b.items() if key != "operation"})


if __name__ == "__main__":
    unittest.main()
