import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import lookup_guard_managed_throw


class LookupGuardManagedThrowOracleTests(unittest.TestCase):
    def test_rejects_exception_message_and_effect_mutations(self):
        expected = lookup_guard_managed_throw.observations()
        report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer",
                  "stage": "player", "profile": "lookup-guard-managed-throw",
                  "observations": expected}
        with TemporaryDirectory() as temporary:
            path = Path(temporary) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            self.assertEqual(lookup_guard_managed_throw.verify(
                path, "player", "2021.3.35f1")["status"], "passed")
            for index, field, value in [(0, "producerCalls", True),
                                        (7, "exception", "System.Exception"),
                                        (14, "message", "Missing key: -17"),
                                        (10, "lookupCalls", 1),
                                        (4, "sameString", False),
                                        (21, "result", "hidden"),
                                        (21, "hiddenGetterCalls", 1),
                                        (29, "hiddenGetterCalls", 0)]:
                changed = [dict(row) for row in expected]
                changed[index][field] = value
                report["observations"] = changed
                path.write_text(json.dumps(report), encoding="utf-8")
                with self.assertRaises(ValueError):
                    lookup_guard_managed_throw.verify(path, "player", "2021.3.35f1")
