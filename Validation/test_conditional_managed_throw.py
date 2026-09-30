import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import conditional_managed_throw


def _report(observations):
    return {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer",
            "stage": "player", "profile": "conditional-managed-throw",
            "observations": observations}


class ConditionalManagedThrowOracleTests(unittest.TestCase):
    def test_rejects_typed_result_and_branch_mutations(self):
        expected = conditional_managed_throw.observations()
        with TemporaryDirectory() as temporary:
            path = Path(temporary) / "report.json"
            path.write_text(json.dumps(_report(expected)), encoding="utf-8")
            self.assertEqual(conditional_managed_throw.verify(
                path, "player", "2021.3.35f1")["status"], "passed")

            wrong_type = [dict(row) for row in expected]
            wrong_type[0]["result"] = True
            path.write_text(json.dumps(_report(wrong_type)), encoding="utf-8")
            with self.assertRaises(ValueError):
                conditional_managed_throw.verify(path, "player", "2021.3.35f1")

            wrong_exception = [dict(row) for row in expected]
            wrong_exception[3]["exception"] = "none"
            path.write_text(json.dumps(_report(wrong_exception)), encoding="utf-8")
            with self.assertRaises(ValueError):
                conditional_managed_throw.verify(path, "player", "2021.3.35f1")
