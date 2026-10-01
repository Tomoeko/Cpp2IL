import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import native_virtual_scalar_zero_leaf as oracle


class NativeVirtualScalarZeroLeafOracleTests(unittest.TestCase):
    def verify_rows(self, rows):
        with TemporaryDirectory() as folder:
            path = Path(folder) / "observations.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer",
                                       "stage": "player", "profile": "native-virtual-scalar-zero-leaf",
                                       "observations": rows}))
            return oracle.verify(path, "player", "2021.3.35f1")

    def test_dispatch_cannot_be_replaced_with_a_constant(self):
        rows = oracle.observations()
        control = next(row for row in rows if row.get("route") == "control-interface")
        control["resultBits"] = "00000000"
        with self.assertRaises(ValueError):
            self.verify_rows(rows)

    def test_signed_zero_and_null_receiver_are_distinct_obligations(self):
        for kind, key, value in (("dispatch", "resultBits", "80000000"),
                                  ("null", "exception", "none"),
                                  ("unused-string", "resultBits", "3f800000")):
            rows = oracle.observations()
            next(row for row in rows if row["kind"] == kind)[key] = value
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                self.verify_rows(rows)

    def test_missing_constructor_initializer_or_override_is_rejected(self):
        for key, value in (("methods", 11), ("typeInitializers", 0), ("stringVirtual", False)):
            rows = oracle.observations()
            rows[0][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_rows(rows)
        rows = oracle.observations()
        rows[0]["getters"][-1]["baseOwner"] = oracle.PREFIX + "DerivedZero"
        with self.assertRaises(ValueError):
            self.verify_rows(rows)
        with self.assertRaises(ValueError):
            self.verify_rows(oracle.observations()[:-1])
