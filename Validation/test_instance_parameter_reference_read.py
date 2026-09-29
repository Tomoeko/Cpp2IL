import json
import tempfile
import unittest
from pathlib import Path

import instance_parameter_reference_read


class InstanceParameterReferenceReadOracleTests(unittest.TestCase):
    def test_observation_matrix_covers_all_reference_states(self):
        rows = instance_parameter_reference_read.observations()
        self.assertEqual([row["kind"] for row in rows],
                         ["filled", "empty", "null-values", "null-box",
                          "null-reader"])
        self.assertEqual(rows[0]["numbersFirst"], -(1 << 31))
        for row in rows[-2:]:
            self.assertEqual(row["textFailure"], "NullReferenceException")
            self.assertEqual(row["payloadFailure"], "NullReferenceException")
            self.assertEqual(row["numbersFailure"], "NullReferenceException")

    def test_oracle_rejects_wrong_reference_identity(self):
        rows = instance_parameter_reference_read.observations()
        rows[0]["payloadSame"] = False
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "report.json"
            report.write_text(json.dumps({
                "unityVersion": "2021.3.35f1", "stage": "player",
                "platform": "WindowsPlayer",
                "profile": "instance-parameter-reference-read",
                "observations": rows,
            }), encoding="utf-8")
            with self.assertRaises(ValueError):
                instance_parameter_reference_read.verify(
                    report, "player", "2021.3.35f1")

    def test_oracle_rejects_neighbor_side_effect(self):
        rows = instance_parameter_reference_read.observations()
        rows[2]["neighborAfter"] = 0
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "report.json"
            report.write_text(json.dumps({
                "unityVersion": "2021.3.35f1", "stage": "player",
                "platform": "WindowsPlayer",
                "profile": "instance-parameter-reference-read",
                "observations": rows,
            }), encoding="utf-8")
            with self.assertRaises(ValueError):
                instance_parameter_reference_read.verify(
                    report, "player", "2021.3.35f1")
