"""Read-only Wine path mapping and fallback boundary regressions."""

import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

import run_fixture as fixture


@unittest.skipIf(os.name == "nt", "Wine drive symlinks are a POSIX host path")
class WinePathTranslationTests(unittest.TestCase):
    def setUp(self):
        scratch = fixture.ROOT / "Files/validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.prefix = self.root / "synthetic prefix"
        self.devices = self.prefix / "dosdevices"
        self.devices.mkdir(parents=True)
        self.path = self.root / "project with spaces/Reports/result.json"
        self.path.parent.mkdir(parents=True)
        self.environment = {"WINEPREFIX": str(self.prefix), "WINEDEBUG": "-all"}
        self.wine = "configured-wine"

    def mapping(self, letter, root):
        (self.devices / (letter + ":")).symlink_to(root, target_is_directory=True)

    def translate(self, timeout=30):
        return fixture.translate_target_path(self.path, self.wine, self.environment, timeout)

    def test_root_drive_mapping_avoids_wine_launch_and_preserves_prefix(self):
        self.mapping("z", Path("/"))
        sentinel = self.prefix / "unchanged-state"
        sentinel.write_bytes(b"synthetic prefix state")
        original_environment = self.environment.copy()
        original_mapping = (self.devices / "z:").readlink()
        with mock.patch.object(fixture.subprocess, "run") as run:
            self.assertEqual(self.translate(), "Z:" + str(self.path).replace("/", "\\"))
            run.assert_not_called()
        self.assertEqual(self.environment, original_environment)
        self.assertEqual((self.devices / "z:").readlink(), original_mapping)
        self.assertEqual(sentinel.read_bytes(), b"synthetic prefix state")

    def test_unique_longest_mapping_and_exact_drive_root_are_used(self):
        self.mapping("z", Path("/"))
        self.mapping("q", self.root)
        self.mapping("r", self.path.parent)
        with mock.patch.object(fixture.subprocess, "run") as run:
            self.assertEqual(self.translate(), "R:\\result.json")
            self.assertEqual(fixture.translate_target_path(self.path.parent, self.wine, self.environment), "R:\\")
            run.assert_not_called()

    def test_drive_matching_uses_path_components_not_string_prefixes(self):
        target = self.root / "project"
        target.mkdir()
        self.mapping("q", target)
        with mock.patch.object(fixture.subprocess, "run", return_value=mock.Mock(stdout="Z:\\fallback\n")) as run:
            self.assertEqual(self.translate(), "Z:\\fallback")
            run.assert_called_once()

    def test_missing_or_ambiguous_mapping_uses_remaining_fallback_timeout(self):
        with mock.patch.object(fixture.time, "monotonic", side_effect=(100.0, 100.25, 100.5)), \
                mock.patch.object(fixture.subprocess, "run", return_value=mock.Mock(stdout="Z:\\fallback\n")) as run:
            self.assertEqual(self.translate(2.75), "Z:\\fallback")
            run.assert_called_once_with([self.wine, "winepath", "-w", str(self.path)], env=self.environment,
                                        capture_output=True, text=True, timeout=2.5, check=True)
        self.mapping("q", self.root)
        self.mapping("r", self.root)
        with mock.patch.object(fixture.subprocess, "run", return_value=mock.Mock(stdout="Q:\\fallback\n")) as run:
            self.assertEqual(self.translate(), "Q:\\fallback")
            run.assert_called_once()

    def test_missing_prefix_and_linked_device_directory_use_fallback(self):
        missing = {"WINEPREFIX": str(self.root / "missing prefix")}
        with mock.patch.object(fixture.subprocess, "run", return_value=mock.Mock(stdout="Z:\\fallback\n")) as run:
            fixture.translate_target_path(self.path, self.wine, missing)
            run.assert_called_once()
            self.assertFalse(Path(missing["WINEPREFIX"]).exists())
        self.devices.rename(self.prefix / "retained devices")
        self.devices.symlink_to(self.prefix / "retained devices", target_is_directory=True)
        with mock.patch.object(fixture.subprocess, "run", return_value=mock.Mock(stdout="Z:\\fallback\n")) as run:
            self.translate()
            run.assert_called_once()

    def test_missing_parent_uses_winepath_instead_of_synthesizing_a_drive_path(self):
        self.mapping("z", Path("/"))
        path = self.root / "missing parent/deeper/result.json"
        with mock.patch.object(fixture.subprocess, "run", return_value=mock.Mock(stdout="Z:\\fallback\n")) as run:
            self.assertEqual(fixture.translate_target_path(path, self.wine, self.environment), "Z:\\fallback")
            run.assert_called_once()
        with mock.patch.object(fixture.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "winepath")):
            with self.assertRaises(subprocess.CalledProcessError):
                fixture.translate_target_path(path, self.wine, self.environment)

    def test_broken_or_raw_device_mappings_do_not_disable_valid_root_mapping(self):
        self.mapping("z", Path("/"))
        self.mapping("f", self.root / "missing volume")
        (self.devices / "d::").symlink_to(self.root / "raw-device")
        with mock.patch.object(fixture.subprocess, "run") as run:
            self.assertEqual(self.translate(), "Z:" + str(self.path).replace("/", "\\"))
            run.assert_not_called()

    def test_unproved_directory_mapping_falls_back_and_mapping_changes_are_rechecked(self):
        self.mapping("z", Path("/"))
        (self.devices / "c:").mkdir()
        with mock.patch.object(fixture.subprocess, "run", return_value=mock.Mock(stdout="Z:\\fallback\n")) as run:
            self.translate()
            run.assert_called_once()
        (self.devices / "c:").rmdir()
        self.mapping("q", self.path.parent)
        self.assertEqual(self.translate(), "Q:\\result.json")
        (self.devices / "q:").unlink()
        self.assertEqual(self.translate(), "Z:" + str(self.path).replace("/", "\\"))

    def test_invalid_host_or_windows_result_paths_are_rejected(self):
        with mock.patch.object(fixture.subprocess, "run") as run:
            for name in ("alternate:stream", "back\\slash", "trailing.", "NUL", "line\nbreak"):
                with self.subTest(name=name), self.assertRaisesRegex(ValueError, "represented safely"):
                    fixture.translate_target_path(self.root / name, self.wine, self.environment)
            run.assert_not_called()
        for value in ("", "relative\\path", "Z:relative", "Z:\\line\nbreak", "\\\\bad?server\\share\\file"):
            with self.subTest(result=value), mock.patch.object(
                    fixture.subprocess, "run", return_value=mock.Mock(stdout=value)), self.assertRaisesRegex(
                        ValueError, "invalid absolute Windows path"):
                self.translate()

    def test_timeout_and_process_failure_are_not_hidden(self):
        with mock.patch.object(fixture.subprocess, "run", side_effect=subprocess.TimeoutExpired("winepath", 1)):
            with self.assertRaises(subprocess.TimeoutExpired):
                self.translate(1)
        with mock.patch.object(fixture.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "winepath")):
            with self.assertRaises(subprocess.CalledProcessError):
                self.translate()
        self.mapping("z", Path("/"))
        with self.assertRaisesRegex(ValueError, "timeout must be positive"):
            self.translate(0)

    def test_mapping_scan_consumes_deadline_before_fallback_or_direct_return(self):
        for mapped in (None, "Z:\\mapped"):
            with self.subTest(mapped=mapped), mock.patch.object(fixture, "_mapped_wine_path", return_value=mapped), \
                    mock.patch.object(fixture.time, "monotonic", side_effect=(100.0, 101.0)), \
                    mock.patch.object(fixture.subprocess, "run") as run:
                with self.assertRaises(subprocess.TimeoutExpired) as error:
                    self.translate(1)
                self.assertEqual(error.exception.timeout, 1)
                run.assert_not_called()

    def test_fallback_result_processing_cannot_overrun_translation_deadline(self):
        with mock.patch.object(fixture.time, "monotonic", side_effect=(100.0, 100.25, 101.0)), \
                mock.patch.object(fixture.subprocess, "run", return_value=mock.Mock(stdout="Z:\\fallback\n")) as run:
            with self.assertRaises(subprocess.TimeoutExpired):
                self.translate(1)
            self.assertEqual(run.call_args.kwargs["timeout"], 0.75)

    def test_native_paths_and_valid_unc_fallback_keep_existing_behavior(self):
        with mock.patch.object(fixture.subprocess, "run") as run:
            self.assertEqual(fixture.translate_target_path(self.path, None, self.environment), str(self.path))
            run.assert_not_called()
        with mock.patch.object(fixture.subprocess, "run", return_value=mock.Mock(stdout="\\\\server\\share\\file\n")):
            self.assertEqual(self.translate(), "\\\\server\\share\\file")


if __name__ == "__main__":
    unittest.main()
