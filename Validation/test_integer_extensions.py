import copy
import json
from pathlib import Path
import unittest
from unittest.mock import Mock

from integer_extensions import observations, verify


class IntegerExtensionOracleTests(unittest.TestCase):
    def report(self):
        return {"unityVersion": "2021.3.35f1", "stage": "player", "platform": "WindowsPlayer",
                "profile": "integer-extensions", "observations": observations()}

    def verify(self, report):
        return verify(Mock(spec=Path, read_text=Mock(return_value=json.dumps(report))), "player", "2021.3.35f1")

    def test_all_byte_patterns_and_boundary_denominators(self):
        result = self.verify(self.report())
        self.assertEqual((result["observations"], result["resultChecks"], result["methods"]), (280, 1364, 12))
        rows = observations()
        self.assertEqual(rows[128]["sign32"], "ffffff80")
        self.assertEqual(rows[128]["sign64"], "ffffffffffffff80")
        self.assertEqual(rows[128]["zero64"], "0000000000000080")
        self.assertEqual(rows[-1]["signU64"], "ffffffffffffffff")
        self.assertEqual(rows[-1]["zero64"], "00000000ffffffff")

    def test_wrong_extension_missing_duplicate_and_numeric_outputs_reject(self):
        original = self.report()
        changed = copy.deepcopy(original)
        changed["observations"][128]["sign64"] = "00000000ffffff80"
        missing = copy.deepcopy(original)
        missing["observations"].pop()
        duplicate = copy.deepcopy(original)
        duplicate["observations"].append(duplicate["observations"][0])
        numeric = copy.deepcopy(original)
        numeric["observations"][0]["zero64"] = 0
        for report in (changed, missing, duplicate, numeric):
            with self.subTest(report=report["observations"][-1]), self.assertRaisesRegex(ValueError, "bit-pattern oracle"):
                self.verify(report)

    def test_wrong_identity_or_player_platform_rejects(self):
        for key, value in (("unityVersion", "2021.3.34f1"), ("stage", "editor"),
                           ("profile", "arithmetic"), ("platform", "WindowsEditor")):
            report = self.report()
            report[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify(report)


if __name__ == "__main__":
    unittest.main()
