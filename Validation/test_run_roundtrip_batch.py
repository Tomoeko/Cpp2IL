"""Regression checks for authenticating a prepared recovery's Unity batch gates."""

import copy
import json
from pathlib import Path
import tempfile
import unittest

from run_fixture import VERSION, verify_behavior
from run_roundtrip import current_source_files, digest
from run_roundtrip_batch import BUILD_SETTINGS, checked_batch_profile, verify_batch_player
from static_scalar_setter import observations


class CheckedBatchProfileTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        self.directory = root / "prepared"
        self.batch_directory = root / "batch"
        self.profile = "static-scalar-setter"
        self.assembly = "StaticScalarSetterFixture"
        self.source = (self.directory / "recovered/UnityProject/Assets/Recovered" /
                       self.assembly)
        self.compiled_source = self.batch_directory / "project/Assets" / self.assembly
        source_files = {
            "Values.cs": b"public static class Values { public static int Value; }\n",
            self.assembly + ".asmdef": json.dumps({"name": self.assembly}).encode(),
            "csc.rsp": b"-langversion:9.0\n",
        }
        for relative, content in source_files.items():
            self.write(self.source / relative, content)
            self.write(self.compiled_source / relative, content)
        build = {
            "status": "passed", "unityVersion": VERSION, "host": "WindowsEditor",
            "target": "StandaloneWindows64", "backend": "IL2CPP",
            "compilerConfiguration": "Release", "apiCompatibility": "NET_Unity_4_8",
            "stripping": "Low", "codeGeneration": "OptimizeSpeed", "stripEngineCode": True,
            "scriptingDefineSymbols": "", "development": False, "errors": 0,
            "result": "Succeeded",
        }
        stages = {"unityCompilation": {"status": "passed", "version": VERSION},
                  "nativeBuild": build}
        self.behavior_paths = {}
        for stage, name, platform in (("editor", "editorBehavior", "WindowsEditor"),
                                      ("player", "playerBehavior", "WindowsPlayer")):
            path = (self.batch_directory / "project/Reports/batch" / self.profile /
                    "editor-behavior.json" if stage == "editor" else
                    self.batch_directory / "player-behavior" / (self.profile + ".json"))
            report = {"unityVersion": VERSION, "stage": stage, "platform": platform,
                      "profile": self.profile, "observations": observations()}
            self.write(path, json.dumps(report).encode())
            self.behavior_paths[name] = path
            stages[name] = verify_behavior(path, stage, self.profile)
        self.prepared = {
            "status": "awaiting-unity-batch", "profile": self.profile,
            "scope": self.assembly, "batchSourceDirectory": str(self.source),
            "stages": {"original": copy.deepcopy(stages)},
            "inputFiles": [], "toolFiles": [], "comparisonToolFiles": [],
            "artifacts": [], "managedOracles": {"files": {}},
        }
        self.batch = {"status": "passed", "profiles": {self.profile: {
            "assembly": self.assembly,
            "sourceFiles": [{"path": name, "sha256": sha256}
                            for name, sha256 in current_source_files(self.source).items()],
            "behaviorReports": [{"path": path.relative_to(self.batch_directory).as_posix(),
                                 "sha256": digest(path)} for path in self.behavior_paths.values()],
            "stages": copy.deepcopy(stages),
        }}}
        for key, prefix, relative in (
                ("inputFiles", "recovery-input", "GameAssembly.dll"),
                ("inputFiles", "recovery-input", "RecoveryFixture_Data/il2cpp_data/Metadata/global-metadata.dat"),
                ("toolFiles", "tool", "Cpp2IL.Core.dll"),
                ("comparisonToolFiles", "declaration-comparer", "DeclarationComparer.dll")):
            path = self.directory / prefix / relative
            self.write(path, b"neutral evidence snapshot\n")
            self.prepared[key].append({"path": relative, "sha256": digest(path)})
        for path in sorted(self.source.iterdir()):
            self.prepared["artifacts"].append({
                "path": str(path.relative_to(self.directory)), "sha256": digest(path)})
        for kind in ("stripped", "unstripped"):
            path = self.directory / "validation-oracles" / kind / (self.assembly + ".dll")
            self.write(path, b"neutral managed validation oracle\n")
            self.prepared["managedOracles"]["files"][kind] = {
                "path": str(path.relative_to(self.directory)), "sha256": digest(path)}

    @staticmethod
    def write(path, content):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    def check(self, prepared=None, batch=None):
        return checked_batch_profile(self.directory, prepared or self.prepared,
                                     self.batch_directory, batch or self.batch)

    def update_behavior_hash(self, path):
        relative = path.relative_to(self.batch_directory).as_posix()
        for record in self.batch["profiles"][self.profile]["behaviorReports"]:
            if record["path"] == relative:
                record["sha256"] = digest(path)

    def test_accepts_independently_verified_source_and_exact_target_reports(self):
        self.assertEqual(self.check(), self.batch["profiles"][self.profile]["stages"])

    def test_requires_pending_recovery_and_passed_batch(self):
        for prepared_status, batch_status in (("passed", "passed"),
                                               ("awaiting-unity-batch", "failed")):
            with self.subTest(prepared=prepared_status, batch=batch_status):
                prepared, batch = copy.deepcopy(self.prepared), copy.deepcopy(self.batch)
                prepared["status"], batch["status"] = prepared_status, batch_status
                with self.assertRaises(ValueError):
                    self.check(prepared, batch)

    def test_rejects_changed_assembly_or_source_directory(self):
        prepared = copy.deepcopy(self.prepared)
        prepared["scope"] = "OtherFixture"
        with self.assertRaises(ValueError):
            self.check(prepared)
        batch = copy.deepcopy(self.batch)
        batch["profiles"][self.profile]["assembly"] = "OtherFixture"
        with self.assertRaises(ValueError):
            self.check(batch=batch)
        prepared = copy.deepcopy(self.prepared)
        prepared["batchSourceDirectory"] = str(self.compiled_source)
        with self.assertRaises(ValueError):
            self.check(prepared)

    def test_rejects_duplicate_source_inventory(self):
        batch = copy.deepcopy(self.batch)
        files = batch["profiles"][self.profile]["sourceFiles"]
        files[-1] = copy.deepcopy(files[0])
        with self.assertRaises(ValueError):
            self.check(batch=batch)

    def test_rejects_modified_or_extra_compiled_source(self):
        path = self.compiled_source / "Values.cs"
        original = path.read_bytes()
        path.write_bytes(original + b"// changed source\n")
        with self.assertRaises(ValueError):
            self.check()
        path.write_bytes(original)
        self.write(self.compiled_source / "Additional.cs", b"public class Additional {}\n")
        with self.assertRaises(ValueError):
            self.check()

    def test_requires_every_gate_and_exact_original_build_settings(self):
        for missing in ("unityCompilation", "nativeBuild", "editorBehavior", "playerBehavior"):
            with self.subTest(missing=missing):
                batch = copy.deepcopy(self.batch)
                del batch["profiles"][self.profile]["stages"][missing]
                with self.assertRaises(ValueError):
                    self.check(batch=batch)
        for key in BUILD_SETTINGS:
            with self.subTest(setting=key):
                batch = copy.deepcopy(self.batch)
                batch["profiles"][self.profile]["stages"]["nativeBuild"][key] = "changed"
                with self.assertRaises(ValueError):
                    self.check(batch=batch)
        for key, value in (("result", "Failed"), ("errors", 1), ("status", "not-run")):
            with self.subTest(build=key):
                batch = copy.deepcopy(self.batch)
                batch["profiles"][self.profile]["stages"]["nativeBuild"][key] = value
                with self.assertRaises(ValueError):
                    self.check(batch=batch)

    def test_rejects_changed_behavior_method_or_observation_denominator(self):
        for name in self.behavior_paths:
            for key in ("methods", "observations"):
                with self.subTest(stage=name, denominator=key):
                    batch = copy.deepcopy(self.batch)
                    batch["profiles"][self.profile]["stages"][name][key] += 1
                    with self.assertRaises(ValueError):
                        self.check(batch=batch)

    def test_rechecks_retained_behavior_types_effects_and_stage(self):
        for name, path in self.behavior_paths.items():
            original = path.read_bytes()
            for field, value in (("value", True), ("value", 17.0), ("before", 0)):
                with self.subTest(stage=name, field=field, value=value):
                    report = json.loads(original)
                    report["observations"][2][field] = value
                    path.write_text(json.dumps(report), encoding="utf-8")
                    self.update_behavior_hash(path)
                    with self.assertRaises(ValueError):
                        self.check()
            report = json.loads(original)
            report["stage"] = "other"
            path.write_text(json.dumps(report), encoding="utf-8")
            self.update_behavior_hash(path)
            with self.assertRaises(ValueError):
                self.check()
            path.write_bytes(original)
            self.update_behavior_hash(path)

    def test_strict_behavior_parser_rejects_duplicates_and_nonstandard_numbers(self):
        for path in self.behavior_paths.values():
            original = path.read_text(encoding="utf-8")
            invalid = [original.replace('"value": 17', '"value": 0, "value": 17', 1),
                       original[:-1] + ', "extra": NaN}',
                       original[:-1] + ', "extra": Infinity}']
            for text in invalid:
                with self.subTest(report=path.name, invalid=text[-30:]):
                    path.write_text(text, encoding="utf-8")
                    self.update_behavior_hash(path)
                    with self.assertRaises(ValueError):
                        self.check()
            path.write_text(original, encoding="utf-8")
            self.update_behavior_hash(path)

    def test_rejects_changed_report_bytes_with_unchanged_observations(self):
        path = self.behavior_paths["playerBehavior"]
        path.write_bytes(path.read_bytes() + b"\n")
        with self.assertRaises(ValueError):
            self.check()

    def test_rejects_missing_or_linked_behavior_report(self):
        path = self.behavior_paths["playerBehavior"]
        original = path.read_bytes()
        path.unlink()
        with self.assertRaises(ValueError):
            self.check()
        target = path.parent / "other.json"
        target.write_bytes(original)
        path.symlink_to(target)
        with self.assertRaises(ValueError):
            self.check()

    def test_rejects_changed_input_tool_source_and_managed_oracle_snapshots(self):
        paths = [self.directory / "recovery-input/GameAssembly.dll",
                 self.directory / "tool/Cpp2IL.Core.dll",
                 self.directory / "declaration-comparer/DeclarationComparer.dll",
                 self.source / "Values.cs",
                 self.directory / "validation-oracles/stripped" / (self.assembly + ".dll")]
        for path in paths:
            with self.subTest(evidence=path.name):
                original = path.read_bytes()
                path.write_bytes(original + b"changed\n")
                with self.assertRaises(ValueError):
                    self.check()
                path.write_bytes(original)

    def test_rejects_changed_source_even_when_batch_inventory_is_updated(self):
        for root in (self.source, self.compiled_source):
            path = root / "Values.cs"
            path.write_bytes(path.read_bytes() + b"// matching altered source\n")
        self.batch["profiles"][self.profile]["sourceFiles"] = [
            {"path": name, "sha256": sha256}
            for name, sha256 in current_source_files(self.source).items()]
        with self.assertRaises(ValueError):
            self.check()

    def test_rejects_snapshot_paths_that_escape_owned_directory(self):
        outside = self.directory.parent / "outside.dll"
        self.write(outside, b"neutral evidence snapshot\n")
        for escaped in (str(outside), "../../outside.dll"):
            with self.subTest(path=escaped):
                prepared = copy.deepcopy(self.prepared)
                prepared["toolFiles"][0] = {"path": escaped, "sha256": digest(outside)}
                with self.assertRaises(ValueError):
                    self.check(prepared)

    def test_rejects_linked_snapshot_root_with_matching_bytes(self):
        root = self.directory / "tool"
        moved = self.directory.parent / "other-tool"
        root.rename(moved)
        root.symlink_to(moved, target_is_directory=True)
        with self.assertRaises(ValueError):
            self.check()


class BatchPlayerInputTests(unittest.TestCase):
    def test_requires_complete_unique_unchanged_native_player_inputs(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            files = []
            for relative in ("GameAssembly.dll", "RecoveryFixture.exe", "UnityPlayer.dll",
                             "RecoveryFixture_Data/il2cpp_data/Metadata/global-metadata.dat"):
                path = directory / "player-input" / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"neutral player snapshot\n")
                files.append({"path": relative, "sha256": digest(path)})
            batch = {"playerInputs": files}
            verify_batch_player(directory, batch)
            for changed in ([], files[:-1], files + [files[0]]):
                with self.subTest(inventory_length=len(changed)):
                    with self.assertRaises(ValueError):
                        verify_batch_player(directory, {"playerInputs": changed})
            player = directory / "player-input/GameAssembly.dll"
            player.write_bytes(b"changed native player\n")
            with self.assertRaises(ValueError):
                verify_batch_player(directory, batch)

    def test_rejects_linked_native_player_root_with_matching_recorded_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            root = directory / "player-input"
            files = []
            for relative in ("GameAssembly.dll", "RecoveryFixture.exe", "UnityPlayer.dll",
                             "RecoveryFixture_Data/il2cpp_data/Metadata/global-metadata.dat"):
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"neutral player snapshot\n")
                files.append({"path": relative, "sha256": digest(path)})
            moved = directory / "other-player"
            root.rename(moved)
            root.symlink_to(moved, target_is_directory=True)
            with self.assertRaises(ValueError):
                verify_batch_player(directory, {"playerInputs": files})


if __name__ == "__main__":
    unittest.main()
