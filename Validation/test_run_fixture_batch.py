import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock
from xml.etree import ElementTree

import run_fixture as fixture
import run_fixture_batch as batch


class FixtureBatchTests(unittest.TestCase):
    def configuration(self, directory, entries):
        path = directory / "fixtures.json"
        path.write_text(json.dumps({"profiles": entries}), encoding="utf-8")
        return path

    def entry(self, name):
        return {"profile": name, "sourceDirectory": str(fixture.PROFILES[name]["source"])}

    def test_duplicate_profiles_and_assembly_boundaries_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            path = self.configuration(directory, [self.entry("static-field-getter")] * 2)
            with self.assertRaisesRegex(ValueError, "known and unique"):
                batch.load_profiles(path)
            source = directory / "changed-source"
            fixture.copy_sources(fixture.PROFILES["static-field-getter"]["source"], source)
            definition = source / "StaticFieldGetterFixture.asmdef"
            contents = json.loads(definition.read_text())
            contents["name"] = "OtherFixture"
            definition.write_text(json.dumps(contents), encoding="utf-8")
            path = self.configuration(directory, [{"profile": "static-field-getter", "sourceDirectory": str(source)}])
            with self.assertRaisesRegex(ValueError, "assembly identity"):
                batch.load_profiles(path)
            contents["name"] = "StaticFieldGetterFixture"
            definition.write_text(json.dumps(contents), encoding="utf-8")
            (source / "Extra.asmref").write_text('{"reference":"OtherFixture"}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "exactly one"):
                batch.load_profiles(path)

    def test_harness_isolation_preserves_fixture_and_behavior_source_bytes(self):
        names = ("static-field-getter", "virtual-string-call", "guarded-sink")
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            profiles = batch.load_profiles(self.configuration(directory, [self.entry(name) for name in names]))
            receipt = {"profiles": {name: {} for name in names}}
            project = directory / "project"
            batch.prepare_project(project, profiles, receipt)
            for item in profiles:
                name, assembly = item["profile"], item["assembly"]
                record = receipt["profiles"][name]
                source = project / "Assets" / assembly
                for copied in record["sourceFiles"]:
                    self.assertEqual((source / copied["path"]).read_bytes(),
                                     (item["sourceDirectory"] / copied["path"]).read_bytes())
                harness = project / "Assets/BatchHarnesses" / assembly
                definition = json.loads((harness / "Runtime/RecoveryValidation.Runtime.asmdef").read_text())
                self.assertEqual(definition["name"], "RecoveryValidation.Runtime." + assembly)
                self.assertFalse(definition["autoReferenced"])
                self.assertEqual((harness / "Runtime/BehaviorProbe.cs").read_bytes(),
                                 (fixture.profile_harness_directory(name) / "Runtime/BehaviorProbe.cs").read_bytes())
                batch.verify_inventory(harness, record["harnessFiles"])
            central = project / "Assets/Validation"
            self.assertEqual((central / "Editor/ValidationEntry.cs").read_bytes(),
                             (fixture.VALIDATION / "Harness/Editor/ValidationEntry.cs").read_bytes())
            self.assertEqual(json.loads((central / "Runtime/RecoveryValidation.Runtime.asmdef").read_text())["references"], [])
            preserved = ElementTree.parse(central / "link.xml").getroot()
            self.assertEqual({node.attrib["fullname"] for node in preserved},
                             {"RecoveryValidation.Runtime." + item["assembly"] for item in profiles})
            for node in preserved:
                self.assertEqual(node[0].attrib, {"fullname": "RecoveryValidation.BehaviorProbe", "preserve": "all"})
            self.assertEqual(receipt["packageGraph"]["packages"],
                             ["com.example.cast-hierarchy", "com.example.guarded-sink"])
            self.assertFalse(receipt["packageGraph"]["matchesIndividualBaselineGraph"])
            for name in names[1:]:
                config = fixture.EMBEDDED_FIXTURE_PACKAGES[name]
                for original in receipt["profiles"][name]["auxiliaryDependencies"]["embeddedPackageFiles"]:
                    self.assertEqual((project / "Packages" / config["name"] / original["path"]).read_bytes(),
                                     (config["source"] / config["name"] / original["path"]).read_bytes())
            probe = project / "Assets/BatchHarnesses/StaticFieldGetterFixture/Runtime/BehaviorProbe.cs"
            probe.write_text(probe.read_text() + "\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "inventory changed"):
                batch.verify_inventory(probe.parent.parent, receipt["profiles"][names[0]]["harnessFiles"])

    def test_duplicate_configuration_keys_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "fixtures.json"
            path.write_text('{"profiles":[],"profiles":[]}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Duplicate configuration key"):
                batch.load_profiles(path)

    @unittest.skipIf(os.name == "nt", "Wine editor locking is a POSIX host gate")
    def test_editor_queue_deadline_releases_for_a_later_run(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "Files").mkdir()
            lock = directory / "Files/windows-unity-editor.lock"
            code = ("import fcntl,sys\n"
                    "with open(sys.argv[1], 'a+b') as lock:\n"
                    " fcntl.flock(lock.fileno(), fcntl.LOCK_EX)\n"
                    " print('held', flush=True)\n"
                    " sys.stdin.readline()\n")
            process = subprocess.Popen([sys.executable, "-c", code, str(lock)],
                                       stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                self.assertEqual(process.stdout.readline().strip(), "held")
                with mock.patch.object(fixture, "ROOT", directory):
                    started = time.monotonic()
                    with self.assertRaises(TimeoutError):
                        with fixture.wine_editor_slot(True, timeout=0.2):
                            self.fail("A concurrent editor lock was acquired")
                    elapsed = time.monotonic() - started
                    self.assertGreaterEqual(elapsed, 0.18)
                    self.assertLess(elapsed, 1.0)
                    process.stdin.write("\n")
                    process.stdin.flush()
                    self.assertEqual(process.wait(timeout=2), 0)
                    with fixture.wine_editor_slot(True, timeout=0.2) as queue_seconds:
                        self.assertLess(queue_seconds, 0.2)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=2)
                for stream in (process.stdin, process.stdout, process.stderr):
                    stream.close()


if __name__ == "__main__":
    unittest.main()
