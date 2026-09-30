import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import call_result_boolean_tail


class CallResultBooleanTailOracleTests(unittest.TestCase):
    def test_argument_effect_and_null_order_mutations_fail(self):
        report = {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                  "profile": "call-result-boolean-tail", "observations": call_result_boolean_tail.observations()}
        with TemporaryDirectory() as temporary:
            path = Path(temporary) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            self.assertEqual(call_result_boolean_tail.verify(path, "player", "2021.3.35f1")["observations"], 29)
            for index, key, wrong in ((2, "value", True), (6, "value", False),
                                      (15, "producerCount", 12), (21, "producerCount", 19),
                                      (15, "applyCount", 14), (28, "value", False),
                                      (2, "applyCount", True)):
                changed = json.loads(json.dumps(report))
                changed["observations"][index][key] = wrong
                path.write_text(json.dumps(changed), encoding="utf-8")
                with self.subTest(index=index, key=key):
                    with self.assertRaises(ValueError):
                        call_result_boolean_tail.verify(path, "player", "2021.3.35f1")
