"""Protect receipts when shared harness infrastructure replaces profile files."""

import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import run_fixture


class HarnessSourceInventoryTests(unittest.TestCase):
    def test_shared_serializer_has_one_final_content_record(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary)
            profile = root / "profile"
            runtime = profile / "Runtime"
            runtime.mkdir(parents=True)
            (runtime / "BehaviorProbe.cs").write_text("class BehaviorProbe {}", encoding="utf-8")
            (runtime / "ReportJson.cs").write_text("class ReplacedSerializer {}", encoding="utf-8")
            destination = root / "copied"
            with patch.object(run_fixture, "profile_harness_directory", return_value=profile):
                records = run_fixture.copy_harness("synthetic-profile", destination)
            serializers = [item for item in records if item["path"] == "Runtime/ReportJson.cs"]
            shared = run_fixture.VALIDATION / "Harness" / "Runtime" / "ReportJson.cs"
            expected = hashlib.sha256(shared.read_bytes()).hexdigest()
            self.assertEqual(serializers, [{"path": "Runtime/ReportJson.cs", "sha256": expected}])
            self.assertEqual((destination / "Runtime/ReportJson.cs").read_bytes(), shared.read_bytes())
            self.assertEqual(len(records), len({item["path"] for item in records}))


if __name__ == "__main__":
    unittest.main()
