import copy
import json
from pathlib import Path
import tempfile
import unittest

import reference_array_scalar_reset as oracle


class ReferenceArrayScalarResetOracleTests(unittest.TestCase):
    def setUp(self):
        self.report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                       "profile": "reference-array-scalar-reset", "observations": oracle.observations()}
        self.rows = {(row.get("method"), row["kind"]): row for row in self.report["observations"]}

    def verify(self, report):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_partial_effects_null_order_and_fixed_count(self):
        self.assertEqual((self.verify(self.report)["methods"], len(self.report["observations"])), (5, 36))
        row = self.rows["active", "null-middle"]
        self.assertEqual(row["exception"], "System.NullReferenceException")
        self.assertEqual(row["after"]["markerBits"], 0)
        self.assertIs(row["after"]["flags"][0]["active"], False)
        self.assertIs(row["after"]["flags"][2]["active"], True)
        row = self.rows["values", "short"]
        self.assertEqual(row["exception"], "System.IndexOutOfRangeException")
        self.assertTrue(all(item["valueBits"] == 0 for item in row["after"]["values"]))
        self.assertEqual(row["after"]["markerBits"], oracle.signed(oracle.MARKER_BITS))
        row = self.rows["values", "long"]
        self.assertEqual(row["after"]["values"][7], row["before"]["values"][7])
        self.assertEqual(self.rows["values", "null-after-limit"]["exception"], "none")

    def test_aliases_neighbors_and_reuse_remain_observable(self):
        for method in ("active", "values"):
            row = self.rows[method, "alias"]
            field = "flags" if method == "active" else "values"
            self.assertEqual(row["after"][field][0], row["after"][field][1])
            self.assertEqual([item["neighbor"] for item in row["before"][field]],
                             [item["neighbor"] for item in row["after"][field]])
            self.assertEqual(self.rows[method, "reuse-repeat"]["before"], self.rows[method, "reuse-repeat"]["after"])
        self.assertEqual(self.rows["values", "tail-alias"]["after"]["values"][7]["valueBits"], 0)
        self.assertEqual(self.rows["values", "shared-second"]["before"]["values"][1]["valueBits"], oracle.signed(0x80000001))

    def test_wrong_store_bits_failures_alias_or_field_mutation_reject(self):
        for method, kind, mutation in (
            ("active", "null-middle", lambda row: row["after"]["flags"][2].update(active=False)),
            ("active", "null-array", lambda row: row["after"].update(markerBits=oracle.signed(oracle.MARKER_BITS))),
            ("values", "null-last", lambda row: row["after"]["values"][0].update(valueBits=oracle.signed(0x80000000))),
            ("values", "short", lambda row: row.update(exception="none")),
            ("values", "long", lambda row: row["after"]["values"][7].update(valueBits=0)),
            ("values", "tail-alias", lambda row: row["after"]["values"][7].update(identity=7)),
            ("values", "exact", lambda row: row["after"]["values"][0].update(neighbor=0)),
            ("active", "three", lambda row: row["after"]["flags"][0].update(active=0)),
        ):
            with self.subTest(method=method, kind=kind):
                changed = copy.deepcopy(self.report)
                row = next(row for row in changed["observations"] if row.get("method") == method and row["kind"] == kind)
                mutation(row)
                with self.assertRaises(ValueError): self.verify(changed)


if __name__ == "__main__":
    unittest.main()
