import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import native_instance_byref_throw as oracle


class NativeInstanceByrefThrowOracleTests(unittest.TestCase):
    def test_changed_byref_or_exception_is_rejected(self):
        for field, value in (("value", 1), ("exception", "System.Exception")):
            rows = oracle.observations()
            rows[0][field] = value
            with TemporaryDirectory() as folder:
                path = Path(folder) / "observations.json"
                path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer",
                                           "stage": "player", "profile": "native-instance-byref-throw", "observations": rows}))
                with self.assertRaises(ValueError):
                    oracle.verify(path, "player", "2021.3.35f1")

    def test_alias_change_is_rejected(self):
        rows = oracle.observations()
        rows[7]["firstThirdAlias"] = False
        with TemporaryDirectory() as folder:
            path = Path(folder) / "observations.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "platform": "WindowsEditor",
                                       "stage": "editor", "profile": "native-instance-byref-throw", "observations": rows}))
            with self.assertRaises(ValueError):
                oracle.verify(path, "editor", "2021.3.35f1")
