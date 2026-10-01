"""Small acceptance-boundary regressions; these do not claim Unity validation."""

import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest import mock

import run_fixture
import run_roundtrip


class HarnessBoundaries(unittest.TestCase):
    def setUp(self):
        scratch = run_fixture.ROOT / "Files" / "validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.fixture = self.root / "Fixture"
        self.fixture.mkdir()
        (self.fixture / "Fixture.cs").write_text("public class Fixture {}\n", encoding="utf-8")
        (self.fixture / "RecoveryFixture.asmdef").write_text("{}\n", encoding="utf-8")
        self.harness = self.root / "Harness"
        (self.harness / "Editor").mkdir(parents=True)
        (self.harness / "Runtime").mkdir()
        (self.harness / "Editor" / "ValidationEntry.cs").write_text("public class ValidationEntry {}\n", encoding="utf-8")
        (self.harness / "Runtime" / "ReportJson.cs").write_text("public class ReportJson {}\n", encoding="utf-8")
        fixture_profile = {**run_roundtrip.FIXTURE_PROFILES["arithmetic"], "source": self.fixture}
        profile_patch = mock.patch.dict(run_roundtrip.FIXTURE_PROFILES, {"arithmetic": fixture_profile})
        profile_patch.start()
        self.addCleanup(profile_patch.stop)

    def baseline_receipt(self, profile="arithmetic"):
        player_inputs = []
        for relative in (*run_roundtrip.PLAYER_FILES, "RecoveryFixture.exe", "UnityPlayer.dll"):
            path = self.root / "player-input" / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic player input")
            player_inputs.append({"path": relative, "sha256": run_roundtrip.digest(path)})
        assembly = run_roundtrip.FIXTURE_PROFILES[profile]["assembly"]
        for relative in ("player/RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed",
                         "project/Library/ScriptAssemblies"):
            path = self.root / relative / (assembly + ".dll")
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic managed declaration oracle")
        receipt = {
            "status": "passed", "sourceKind": "synthetic-baseline", "profile": profile,
            "stages": {"unityCompilation": {"status": "passed", "version": run_fixture.VERSION},
                       "editorBehavior": {"status": "passed"},
                       "nativeBuild": {"status": "passed", "unityVersion": run_fixture.VERSION, "host": "WindowsEditor",
                                       "target": "StandaloneWindows64", "backend": "IL2CPP",
                                       "compilerConfiguration": "Release", "codeGeneration": "OptimizeSpeed",
                                       "development": False,
                                       "errors": 0, "result": "Succeeded"},
                       "playerBehavior": {"status": "passed"}},
            "playerInputs": player_inputs,
            "sourceFiles": [{"path": relative, "sha256": run_roundtrip.digest(self.fixture / relative)}
                            for relative in ("Fixture.cs", "RecoveryFixture.asmdef")],
            "harnessFiles": [{"path": relative, "sha256": run_roundtrip.digest(self.harness / relative)}
                             for relative in ("Editor/ValidationEntry.cs", "Runtime/ReportJson.cs")],
        }
        if profile == "arithmetic":
            observations = []
            for left in run_fixture.VALUES:
                for right in run_fixture.VALUES:
                    total = run_fixture.int32(left + right)
                    observations.append({"left": left, "right": right, "add": total,
                                         "select": run_fixture.int32(right - left if left < right else left + right),
                                         "accumulated": total, "stored": total})
        else:
            observations = run_fixture.reference_store.observations()
        receipt["behaviorReports"] = []
        for stage, relative in (("editor", "project/Reports/editor-behavior.json"),
                                ("player", "player-behavior.json")):
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps({"unityVersion": run_fixture.VERSION, "stage": stage,
                                        "platform": "WindowsEditor" if stage == "editor" else "WindowsPlayer",
                                        "profile": profile, "observations": observations}), encoding="utf-8")
            receipt["stages"][stage + "Behavior"] = run_fixture.verify_behavior(path, stage, profile)
            receipt["behaviorReports"].append({"path": relative, "sha256": run_roundtrip.digest(path)})
        return receipt

    def write_baseline_receipt(self, receipt):
        (self.root / "receipt.json").write_text(json.dumps(receipt), encoding="utf-8")

    def exported_source(self, name="exported project"):
        project = self.root / name
        (project / "ProjectSettings").mkdir(parents=True)
        (project / "Packages").mkdir()
        (project / "ProjectSettings/ProjectVersion.txt").write_text(
            "m_EditorVersion: " + run_fixture.VERSION + "\n", encoding="utf-8")
        (project / "Packages/manifest.json").write_text('{"dependencies":{}}', encoding="utf-8")
        source = project / "Assets/Recovered/Source With Spaces"
        source.mkdir(parents=True)
        (source / "Values.cs").write_text("public class Values {}\n", encoding="utf-8")
        return project, source

    def test_exported_source_preserves_nested_response_paths_and_bytes(self):
        project, source = self.exported_source()
        nested = source / "Compiler Inputs/assembly refs.rsp"
        nested.parent.mkdir()
        nested.write_text('-reference:original="Exact References/Dependency.dll"\n', encoding="utf-8")
        response = source / "csc.rsp"
        response.write_text('@"Assets/Recovered/Source With Spaces/Compiler Inputs/assembly refs.rsp"\n',
                            encoding="utf-8")
        (source / "oracle.dll").write_bytes(b"validation-only managed oracle")
        destination = run_fixture.source_destination(source, "RecoveryFixture")
        self.assertEqual(destination, Path("Assets/Recovered/Source With Spaces"))
        compiled = self.root / "compiled project"
        record = {"sourceDestination": destination.as_posix(),
                  "sourceFiles": run_fixture.copy_sources(source, compiled / destination)}
        self.assertEqual(run_fixture.verify_source_copy(compiled, source, "RecoveryFixture", record),
                         compiled / destination)
        for relative in ("csc.rsp", "Compiler Inputs/assembly refs.rsp"):
            self.assertEqual((compiled / destination / relative).read_bytes(), (source / relative).read_bytes())
        self.assertFalse((compiled / destination / "oracle.dll").exists())
        self.assertEqual(run_fixture.source_destination(self.fixture, "RecoveryFixture"),
                         Path("Assets/RecoveryFixture"))
        record["sourceDestination"] = "Assets/RecoveryFixture"
        with self.assertRaisesRegex(ValueError, "original layout"):
            run_fixture.verify_source_copy(compiled, source, "RecoveryFixture", record)

    def test_exported_layout_requires_exact_version_and_valid_manifest(self):
        project, source = self.exported_source()
        version = project / "ProjectSettings/ProjectVersion.txt"
        for text in ("m_EditorVersion: 2021.3.34f1\n", "m_EditorVersion\n",
                     ("m_EditorVersion: " + run_fixture.VERSION + "\n") * 2):
            version.write_text(text, encoding="utf-8")
            with self.subTest(version=text), self.assertRaisesRegex(ValueError, "exact required version"):
                run_fixture.source_destination(source, "RecoveryFixture")
        version.write_text("m_EditorVersion: " + run_fixture.VERSION + "\n", encoding="utf-8")
        manifest = project / "Packages/manifest.json"
        for text in ('[]', '{"dependencies":[]}', '{"dependencies":{"example":1}}',
                     '{"dependencies":{},"dependencies":{}}'):
            manifest.write_text(text, encoding="utf-8")
            with self.subTest(manifest=text), self.assertRaises(ValueError):
                run_fixture.source_destination(source, "RecoveryFixture")
        manifest.unlink()
        with self.assertRaisesRegex(ValueError, "controls are missing"):
            run_fixture.source_destination(source, "RecoveryFixture")

    def test_general_assembly_names_and_intentional_firstpass_layout_are_preserved(self):
        project, source = self.exported_source()
        for assembly in ("Synthetic Application", "Assembly-CSharp", "Assembly-CSharp-firstpass"):
            with self.subTest(assembly=assembly):
                self.assertEqual(run_fixture.source_destination(source, assembly),
                                 Path("Assets/Recovered/Source With Spaces"))
                self.assertEqual(run_fixture.source_destination(self.fixture, assembly), Path("Assets") / assembly)
        firstpass = project / "Assets/Plugins/Recovered/Assembly-CSharp-firstpass"
        firstpass.mkdir(parents=True)
        (firstpass / "Control.cs").write_text("public class Control {}\n", encoding="utf-8")
        (firstpass / "csc.rsp").write_text(
            '@"Assets/Plugins/Recovered/Assembly-CSharp-firstpass/references.rsp"\n', encoding="utf-8")
        (firstpass / "references.rsp").write_text('-reference:original="Exact References/Dependency.dll"\n',
                                                encoding="utf-8")
        relative = Path("Assets/Plugins/Recovered/Assembly-CSharp-firstpass")
        self.assertEqual(run_fixture.source_destination(firstpass, "Assembly-CSharp-firstpass"), relative)
        compiled = self.root / "compiled firstpass"
        record = {"sourceDestination": relative.as_posix(),
                  "sourceFiles": run_fixture.copy_sources(firstpass, compiled / relative)}
        run_fixture.verify_source_copy(compiled, firstpass, "Assembly-CSharp-firstpass", record)
        for path in record["sourceFiles"]:
            self.assertEqual((compiled / relative / path["path"]).read_bytes(),
                             (firstpass / path["path"]).read_bytes())
        for assembly in ("RecoveryFixture", "Assembly-CSharp"):
            with self.subTest(assembly=assembly), self.assertRaisesRegex(ValueError, "infrastructure"):
                run_fixture.source_destination(firstpass, assembly)
        other = project / "Assets/Plugins/Recovered/Other"
        other.mkdir()
        with self.assertRaisesRegex(ValueError, "infrastructure"):
            run_fixture.source_destination(other, "Assembly-CSharp-firstpass")

    def test_exported_source_refuses_unsafe_roots_and_traversal(self):
        project, source = self.exported_source()
        validation = project / "Assets/Validation"
        validation.mkdir()
        for path in (project, project / "Assets", validation, source / ".." / source.name):
            with self.subTest(path=path), self.assertRaises(ValueError):
                run_fixture.source_destination(path, "RecoveryFixture")
        with self.assertRaisesRegex(ValueError, "unsafe"):
            run_fixture.source_destination(source, "../OtherAssembly")
        for name in ("drive:path", "back\\slash", "trailing.", "vALIDATION"):
            path = project / "Assets" / name
            path.mkdir(exist_ok=True)
            with self.subTest(name=name), self.assertRaises(ValueError):
                run_fixture.source_destination(path, "RecoveryFixture")

    @unittest.skipIf(os.name == "nt", "Symlink creation requires host privileges on Windows")
    def test_exported_source_refuses_linked_source_root_and_controls(self):
        project, source = self.exported_source()
        linked = self.root / "linked export"
        linked.symlink_to(project, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "symbolic links"):
            run_fixture.source_destination(linked / source.relative_to(project), "RecoveryFixture")
        for relative in (source.relative_to(project), Path("Assets"), Path("ProjectSettings"),
                         Path("Packages"), Path("ProjectSettings/ProjectVersion.txt"),
                         Path("Packages/manifest.json")):
            path = project / relative
            retained = path.with_name(path.name + ".retained")
            path.rename(retained)
            try:
                path.symlink_to(retained, target_is_directory=retained.is_dir())
                with self.subTest(link=relative), self.assertRaises(ValueError):
                    run_fixture.source_destination(source, "RecoveryFixture")
            finally:
                path.unlink()
                retained.rename(path)

    def test_source_copy_excludes_managed_oracles(self):
        source = self.root / "source"
        source.mkdir()
        for name in ("Recovered.cs", "RecoveryFixture.asmdef", "csc.rsp", "RecoveryFixture.dll", "RecoveryFixture.pdb"):
            (source / name).write_text("synthetic test placeholder")
        manifest = run_fixture.copy_sources(source, self.root / "project")
        self.assertEqual({item["path"] for item in manifest}, {"Recovered.cs", "RecoveryFixture.asmdef", "csc.rsp"})

    def test_managed_oracle_snapshots_survive_baseline_pruning(self):
        baseline = self.root / "baseline"
        assembly = "RecoveryFixture"
        stripped = baseline / "player/RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed" / (assembly + ".dll")
        unstripped = baseline / "project/Library/ScriptAssemblies" / (assembly + ".dll")
        for path, content in ((stripped, b"stripped declarations"),
                              (unstripped, b"unstripped declarations")):
            path.parent.mkdir(parents=True)
            path.write_bytes(content)
        roundtrip = self.root / "roundtrip"
        roundtrip.mkdir()
        references = self.root / "references"
        references.mkdir()
        if os.name != "nt":
            linked_roundtrip = self.root / "linked-roundtrip"
            linked_roundtrip.mkdir()
            (linked_roundtrip / "validation-oracles").symlink_to(self.root / "missing-target")
            with self.assertRaisesRegex(ValueError, "snapshot directory already exists"):
                run_roundtrip.snapshot_managed_oracles(baseline, linked_roundtrip, assembly)

        snapshots = run_roundtrip.snapshot_managed_oracles(
            baseline, roundtrip, assembly, (references,))
        self.assertEqual(set(snapshots), {"stripped", "unstripped"})
        self.assertTrue(all(item["path"].startswith("validation-oracles/")
                            for item in snapshots.values()))
        shutil.rmtree(baseline / "project")
        shutil.rmtree(baseline / "player")
        paths = run_roundtrip.checked_managed_oracle_snapshots(roundtrip, assembly, snapshots)
        self.assertEqual(paths["stripped"].read_bytes(), b"stripped declarations")
        self.assertEqual(paths["unstripped"].read_bytes(), b"unstripped declarations")
        if os.name != "nt":
            outside = self.root / "outside-managed.dll"
            outside.write_bytes(paths["stripped"].read_bytes())
            paths["stripped"].unlink()
            paths["stripped"].symlink_to(outside)
            with self.assertRaisesRegex(ValueError, "oracle snapshot changed"):
                run_roundtrip.checked_managed_oracle_snapshots(roundtrip, assembly, snapshots)
            paths["stripped"].unlink()
            paths["stripped"].write_bytes(b"stripped declarations")

        paths["stripped"].write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "oracle snapshot changed"):
            run_roundtrip.checked_managed_oracle_snapshots(roundtrip, assembly, snapshots)

    def test_player_copy_excludes_source_symbols_and_backup_assemblies(self):
        player = self.root / "player"
        files = ["RecoveryFixture.exe", "GameAssembly.dll", "UnityPlayer.dll",
                 "RecoveryFixture_Data/il2cpp_data/Metadata/global-metadata.dat",
                 "GameAssembly.pdb", "source.cs", "BurstDebugInformation/nested/debug.bin",
                 "RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed/RecoveryFixture.dll"]
        for name in files:
            target = player / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b"synthetic test placeholder")
        with mock.patch.object(run_fixture.storage_budget, "check_headroom") as headroom:
            manifest = run_fixture.isolate_player(player, self.root / "player-input")
        headroom.assert_called_once_with(
            run_fixture.ROOT / "Files", 4 * len(b"synthetic test placeholder")
            + run_fixture.storage_budget.STOP_RESERVE_BYTES)
        self.assertEqual({item["path"] for item in manifest}, set(files[:4]))

    @unittest.skipIf(os.name == "nt", "symbolic link creation requires Windows privileges")
    def test_player_copy_rejects_external_symbolic_links_before_copying(self):
        player = self.root / "player"
        player.mkdir()
        outside = self.root / "outside"
        outside.mkdir()
        (outside / "native.bin").write_bytes(b"external test data")
        for name, target in (("external", outside), ("linked.bin", outside / "native.bin")):
            with self.subTest(name=name):
                link = player / name
                link.symlink_to(target)
                destination = self.root / "player-input"
                with mock.patch.object(run_fixture.storage_budget, "check_headroom") as headroom:
                    with self.assertRaisesRegex(ValueError, "symbolic links"):
                        run_fixture.isolate_player(player, destination)
                headroom.assert_not_called()
                self.assertFalse(destination.exists())
                link.unlink()

    def test_empty_observations_cannot_pass(self):
        report = self.root / "behavior.json"
        report.write_text(json.dumps({"unityVersion": run_fixture.VERSION, "stage": "editor",
                                      "platform": "WindowsEditor", "observations": []}))
        with self.assertRaisesRegex(ValueError, "independent integer oracle"):
            run_fixture.verify_behavior(report, "editor")

    def test_explicit_manifest_baseline_requires_unchanged_resolved_lock(self):
        project = self.root / "project"
        packages = project / "Packages"
        packages.mkdir(parents=True)
        manifest = packages / "manifest.json"
        manifest.write_text('{"dependencies":{"com.example.fixture":"1.0.0"}}', encoding="utf-8")
        lock = packages / "packages-lock.json"
        lock.write_text('{"dependencies":{"com.example.fixture":{"version":"1.0.0"}}}', encoding="utf-8")
        manifest_hash = run_roundtrip.digest(manifest)
        lock_hash = run_fixture.resolved_package_lock_sha256(project)

        receipt = self.baseline_receipt()
        receipt["packageManifest"] = {"provenance": "explicit-auxiliary", "sha256": manifest_hash,
                                      "resolvedLockSha256": lock_hash}
        self.write_baseline_receipt(receipt)
        self.assertEqual(run_roundtrip.checked_baseline(self.root, "arithmetic", manifest_hash), receipt)

        lock.write_text('{"dependencies":{"com.example.fixture":{"version":"1.0.1"}}}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "resolved package lock differs"):
            run_roundtrip.checked_baseline(self.root, "arithmetic", manifest_hash)

        lock.unlink()
        with self.assertRaisesRegex(ValueError, "did not produce a Unity package lock"):
            run_roundtrip.checked_baseline(self.root, "arithmetic", manifest_hash)

    def test_default_baseline_does_not_require_package_lock(self):
        receipt = self.baseline_receipt()
        self.write_baseline_receipt(receipt)
        self.assertEqual(run_roundtrip.checked_baseline(self.root, "arithmetic"), receipt)

    def test_baseline_requires_every_original_gate(self):
        receipt = self.baseline_receipt()
        for name in ("unityCompilation", "editorBehavior", "nativeBuild", "playerBehavior"):
            stage = receipt["stages"][name]
            for status in ("unverified", "not-requested", "failed"):
                with self.subTest(stage=name, status=status):
                    stage["status"] = status
                    self.write_baseline_receipt(receipt)
                    with self.assertRaisesRegex(ValueError, "every original exact-target gate"):
                        run_roundtrip.checked_baseline(self.root, "arithmetic")
            stage["status"] = "passed"
        receipt["stages"]["unityCompilation"]["version"] = "2021.3.34f1"
        self.write_baseline_receipt(receipt)
        with self.assertRaisesRegex(ValueError, "every original exact-target gate"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")

    def test_baseline_rejects_pruned_original_oracles_before_recovery(self):
        receipt = self.baseline_receipt()
        self.write_baseline_receipt(receipt)
        assembly = run_roundtrip.FIXTURE_PROFILES["arithmetic"]["assembly"]
        for relative in ("player/RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed",
                         "project/Library/ScriptAssemblies"):
            path = self.root / relative / (assembly + ".dll")
            original = path.read_bytes()
            path.unlink()
            with self.subTest(oracle=relative), self.assertRaisesRegex(ValueError, "oracle"):
                run_roundtrip.checked_baseline(self.root, "arithmetic")
            path.write_bytes(original)

    def test_baseline_reauthenticates_runtime_inputs_and_inventory(self):
        receipt = self.baseline_receipt()
        for relative in ("RecoveryFixture.exe", "UnityPlayer.dll"):
            path = self.root / "player-input" / relative
            original = path.read_bytes()
            path.write_bytes(b"changed runtime input")
            self.write_baseline_receipt(receipt)
            with self.subTest(input=relative), self.assertRaisesRegex(ValueError, "changed since"):
                run_roundtrip.checked_baseline(self.root, "arithmetic")
            path.write_bytes(original)
        for records in (receipt["playerInputs"][:-1], receipt["playerInputs"] + [receipt["playerInputs"][0]]):
            changed = {**receipt, "playerInputs": records}
            self.write_baseline_receipt(changed)
            with self.subTest(inventory=len(records)), self.assertRaisesRegex(ValueError, "incomplete or duplicated"):
                run_roundtrip.checked_baseline(self.root, "arithmetic")
        self.write_baseline_receipt(receipt)
        (self.root / "player-input/Unexpected.dll").write_bytes(b"unrecorded runtime dependency")
        with self.assertRaisesRegex(ValueError, "path set differs"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")

    @unittest.skipIf(os.name == "nt", "Symlink creation requires host privileges on Windows")
    def test_baseline_rejects_linked_original_oracle_parent(self):
        receipt = self.baseline_receipt()
        self.write_baseline_receipt(receipt)
        original = self.root / "project/Library"
        retained = self.root / "retained-library"
        original.rename(retained)
        original.symlink_to(retained, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "oracle is missing or linked"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")

    def test_baseline_requires_requested_code_generation(self):
        receipt = self.baseline_receipt()
        self.write_baseline_receipt(receipt)
        with self.assertRaisesRegex(ValueError, "required profile"):
            run_roundtrip.checked_baseline(self.root, "arithmetic",
                                           expected_code_generation="OptimizeSize")
        receipt["stages"]["nativeBuild"]["codeGeneration"] = "OptimizeSize"
        self.write_baseline_receipt(receipt)
        self.assertEqual(run_roundtrip.checked_baseline(
            self.root, "arithmetic", expected_code_generation="OptimizeSize"), receipt)

    def test_baseline_requires_supplied_windows_editor_host(self):
        receipt = self.baseline_receipt()
        for host in ("OSXEditor", None):
            with self.subTest(host=host):
                receipt["stages"]["nativeBuild"]["host"] = host
                self.write_baseline_receipt(receipt)
                with self.assertRaisesRegex(ValueError, "required profile"):
                    run_roundtrip.checked_baseline(self.root, "arithmetic")

    def test_baseline_rejects_changed_or_added_fixture_source(self):
        receipt = self.baseline_receipt()
        self.write_baseline_receipt(receipt)
        (self.fixture / "Fixture.cs").write_text("public class ChangedFixture {}\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "fixture source hashes differ"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")
        (self.fixture / "Fixture.cs").write_text("public class Fixture {}\n", encoding="utf-8")
        (self.fixture / "New.cs").write_text("public class NewFixture {}\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "fixture source path set differs"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")

    def test_baseline_rejects_changed_or_missing_harness_source(self):
        receipt = self.baseline_receipt()
        self.write_baseline_receipt(receipt)
        shared = self.harness / "Runtime" / "ReportJson.cs"
        shared.write_text("public class ChangedReportJson {}\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "harness source hashes differ"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")
        shared.unlink()
        with self.assertRaisesRegex(ValueError, "harness source path set differs"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")

    def test_baseline_rejects_missing_source_manifest(self):
        receipt = self.baseline_receipt()
        del receipt["sourceFiles"]
        self.write_baseline_receipt(receipt)
        with self.assertRaisesRegex(ValueError, "fixture source receipt is missing"):
            run_roundtrip.checked_baseline(self.root, "arithmetic")

    def test_profile_harness_includes_shared_editor_and_serializer(self):
        fixture = self.root / "ReferenceStoreFixture"
        fixture.mkdir()
        (fixture / "Store.cs").write_text("public class Store {}\n", encoding="utf-8")
        harness = self.root / "ReferenceStoreHarness" / "Runtime"
        harness.mkdir(parents=True)
        (harness / "BehaviorProbe.cs").write_text("public class BehaviorProbe {}\n", encoding="utf-8")
        profile = {**run_roundtrip.FIXTURE_PROFILES["reference-store"], "source": fixture}
        with mock.patch.dict(run_roundtrip.FIXTURE_PROFILES, {"reference-store": profile}):
            receipt = self.baseline_receipt("reference-store")
            receipt["sourceFiles"] = [{"path": "Store.cs", "sha256": run_roundtrip.digest(fixture / "Store.cs")}]
            receipt["harnessFiles"] = [
                {"path": "Runtime/BehaviorProbe.cs", "sha256": run_roundtrip.digest(harness / "BehaviorProbe.cs")},
                {"path": "Editor/ValidationEntry.cs",
                 "sha256": run_roundtrip.digest(self.harness / "Editor" / "ValidationEntry.cs")},
                {"path": "Runtime/ReportJson.cs",
                 "sha256": run_roundtrip.digest(self.harness / "Runtime" / "ReportJson.cs")},
            ]
            self.write_baseline_receipt(receipt)
            self.assertEqual(run_roundtrip.checked_baseline(self.root, "reference-store"), receipt)
            (self.harness / "Editor" / "ValidationEntry.cs").write_text(
                "public class ChangedValidationEntry {}\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "harness source hashes differ"):
                run_roundtrip.checked_baseline(self.root, "reference-store")

    @unittest.skipIf(os.name == "nt", "POSIX signal-exit regression")
    def test_deadline_is_not_success_when_child_handles_termination(self):
        command = [sys.executable, "-c",
                   "import signal,sys,time; signal.signal(signal.SIGTERM, lambda *_: sys.exit(0)); time.sleep(20)"]
        outcome = run_fixture.run_process(command, os.environ.copy(), self.root / "timeout.log", 1)
        self.assertEqual(outcome["exitCode"], 0)
        self.assertTrue(outcome["timedOut"])


if __name__ == "__main__":
    unittest.main()
