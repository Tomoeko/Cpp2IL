"""Protect the arithmetic oracle's wraparound and declaration denominator."""

import unittest

import open_generic_prefix_operations as fixture


class OpenGenericPrefixOperationsOracleTests(unittest.TestCase):
    def test_observations_cover_overflow_and_later_storage_kinds(self):
        rows = fixture.observations()
        self.assertEqual(len(rows), 67)
        self.assertEqual({row["type"] for row in rows if row["kind"] == "update"},
                         {"int", "long", "string"})
        self.assertTrue(any(row["first"] == (1 << 31) - 1 and row["delta"] == 1 and
                            row["firstAfter"] == -(1 << 31)
                            for row in rows if row["kind"] == "update"))
        self.assertEqual(rows[-1], {"kind": "null", "failure": "System.NullReferenceException"})


if __name__ == "__main__":
    unittest.main()
