import copy
import json
import tempfile
import unittest
from pathlib import Path

import boolean_array_fill_loop


class BooleanArrayFillLoopOracleTests(unittest.TestCase):
    def test_partial_fill_changed_alias_and_null_bypass_fail(self):
        for case in ("last-element", "alias", "null-result", "typed-value"):
            observations = copy.deepcopy(boolean_array_fill_loop.observations())
            if case == "last-element":
                row = next(item for item in observations if item["kind"] == "length-129-true")
                row["after"][-1] = False
            elif case == "alias":
                row = next(item for item in observations if item["kind"] == "shared-literal")
                row["aliasAfter"][0] = True
            elif case == "null-result":
                row = next(item for item in observations if item["kind"] == "null-array-parameter")
                row["exception"] = "none"
            else:
                row = next(item for item in observations if item["kind"] == "length-1-true")
                row["after"][0] = 1
            with tempfile.TemporaryDirectory() as temporary:
                path = Path(temporary) / "behavior.json"
                path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "stage": "player",
                                            "platform": "WindowsPlayer", "profile": "boolean-array-fill-loop",
                                            "observations": observations}), encoding="utf-8")
                with self.assertRaises(ValueError, msg=case):
                    boolean_array_fill_loop.verify(path, "player", "2021.3.35f1")


if __name__ == "__main__":
    unittest.main()
