"""Protect signedness, field identity and caller storage in the fixture oracle."""

import json
from pathlib import Path
import tempfile
import unittest

import narrow_scalar_getter
import run_fixture


class NarrowScalarGetterOracleTests(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name) / "behavior.json"
        self.report = {"unityVersion": run_fixture.VERSION, "stage": "player", "platform": "WindowsPlayer",
                       "profile": "narrow-scalar-getter", "observations": list(narrow_scalar_getter.observations())}

    def verify(self):
        self.path.write_text(json.dumps(self.report), encoding="utf-8")
        return narrow_scalar_getter.verify(self.path, "player", run_fixture.VERSION)

    def test_all_byte_patterns_and_word_sign_boundaries_are_observed(self):
        result = self.verify()
        self.assertEqual(result["observations"], 2112)
        self.assertEqual(result["resultChecks"], 12672)
        for operation in (0, 4):
            rows = [row for row in self.report["observations"] if row["operation"] == operation]
            self.assertEqual(len({row["result"] for row in rows}), 256)
        for operation in (2, 6):
            values = {row["result"] for row in self.report["observations"] if row["operation"] == operation}
            self.assertTrue({-32768, -32767, -2, -1, 32766, 32767} <= values)

    def test_zero_extension_cannot_replace_signed_widening(self):
        row = next(row for row in self.report["observations"] if row["operation"] == 4 and row["input"] == 128)
        row["result"] = 128
        with self.assertRaisesRegex(ValueError, "independent integer oracle"):
            self.verify()

    def test_sign_extension_cannot_replace_unsigned_widening(self):
        row = next(row for row in self.report["observations"] if row["operation"] == 7 and row["input"] == 0)
        row["result"] = -32767
        with self.assertRaisesRegex(ValueError, "independent integer oracle"):
            self.verify()

    def test_wrong_field_and_neighbor_mutations_are_rejected(self):
        self.report["observations"][0]["result"] = self.report["observations"][0]["unsignedByteAfter"]
        with self.assertRaisesRegex(ValueError, "independent integer oracle"):
            self.verify()
        self.report["observations"] = list(narrow_scalar_getter.observations())
        self.report["observations"][0]["neighborAfter"] ^= 1
        with self.assertRaisesRegex(ValueError, "independent integer oracle"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
