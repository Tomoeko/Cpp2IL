import copy
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import ancestor_cctor_interface_getter
import folded_boolean_array_store


def _lose_array_alias_store(rows):
    row = next(row for row in rows if row["case"] == "single-true" and
               row["index"] == 0 and row["owner"] == "first")
    row["aliasAfter"][0] = True


def _change_neighbor(rows):
    rows[0]["markerAfter"] = 0


class FoldedAndAncestorOracleTests(unittest.TestCase):
    def test_mutation_observations_preserve_array_aliases_and_neighbor_fields(self):
        for profile, oracle, change in (
                ("folded-boolean-array-store", folded_boolean_array_store,
                 _lose_array_alias_store),
                ("ancestor-cctor-interface-getter", ancestor_cctor_interface_getter,
                 _change_neighbor)):
            with self.subTest(profile=profile), TemporaryDirectory() as temporary:
                path = Path(temporary) / "report.json"
                report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer",
                          "stage": "player", "profile": profile,
                          "observations": oracle.observations()}
                path.write_text(json.dumps(report), encoding="utf-8")
                self.assertEqual(oracle.verify(path, "player", "2021.3.35f1")["status"],
                                 "passed")
                changed = copy.deepcopy(report)
                change(changed["observations"])
                path.write_text(json.dumps(changed), encoding="utf-8")
                with self.assertRaises(ValueError):
                    oracle.verify(path, "player", "2021.3.35f1")

    def test_ancestor_oracle_rejects_duplicate_json_keys(self):
        report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer",
                  "stage": "player", "profile": "ancestor-cctor-interface-getter",
                  "observations": ancestor_cctor_interface_getter.observations()}
        duplicate = json.dumps(report).replace('"stage": "player"',
                                                '"stage": "player", "stage": "player"')
        with TemporaryDirectory() as temporary:
            path = Path(temporary) / "report.json"
            path.write_text(duplicate, encoding="utf-8")
            with self.assertRaises(ValueError):
                ancestor_cctor_interface_getter.verify(path, "player", "2021.3.35f1")
