"""Protect the editor/player rounding split in the independent float oracle."""

import json
from pathlib import Path
import tempfile
import unittest

import run_fixture
import xmm_ref_mutation


class XmmRefMutationOracleTests(unittest.TestCase):
    def test_rounding_sensitive_results_require_the_correct_stage(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            path = Path(temporary) / "behavior.json"
            for stage, platform, expected in (
                    ("editor", "WindowsEditor", ("40000000", "3f800001")),
                    ("player", "WindowsPlayer", ("3f800000", "3f800000"))):
                observations = xmm_ref_mutation.observations(stage)
                self.assertEqual((observations[5]["sumResult"], observations[12]["sumResult"]),
                                 expected)
                report = {"unityVersion": run_fixture.VERSION, "stage": stage,
                          "platform": platform, "profile": "xmm-ref-mutation",
                          "observations": observations}
                path.write_text(json.dumps(report), encoding="utf-8")
                self.assertEqual(xmm_ref_mutation.verify(path, stage, run_fixture.VERSION)
                                 ["observations"], 13)

                observations[5]["sumResult"] = "3f800000" if stage == "editor" else "40000000"
                path.write_text(json.dumps(report), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "independent bit oracle"):
                    xmm_ref_mutation.verify(path, stage, run_fixture.VERSION)


if __name__ == "__main__":
    unittest.main()
