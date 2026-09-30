"""Regression checks for authenticating a prepared recovery's Unity batch gates."""

import copy
import contextlib
import io
import json
from pathlib import Path
import tempfile
import shutil
import unittest
from unittest import mock

import run_roundtrip_batch as runner
from run_fixture import VERSION, verify_behavior
from run_roundtrip import current_source_files, digest, snapshot_behavior_oracles
from run_roundtrip_batch import BUILD_SETTINGS, checked_batch_profile, checked_final_profile, verify_batch_player
from static_scalar_setter import observations
from native_boolean_toggle_invocation import observations as toggle_observations


class CheckedBatchProfileTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name).resolve()
        self.directory = root / "prepared"
        self.batch_directory = root / "batch"
        self.profile = "static-scalar-setter"
        self.assembly = "StaticScalarSetterFixture"
        self.source = (self.directory / "recovered/UnityProject/Assets/Recovered" /
                       self.assembly)
        self.compiled_source = self.batch_directory / "project/Assets/Recovered" / self.assembly
        export = self.directory / "recovered/UnityProject"
        self.write(export / "ProjectSettings/ProjectVersion.txt", ("m_EditorVersion: " + VERSION + "\n").encode())
        self.write(export / "Packages/manifest.json", b'{"dependencies":{}}')
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
        original_directory = root / "original"
        original_inventory = []
        for stage, name, relative in (("editor", "editorBehavior", "project/Reports/editor-behavior.json"),
                                       ("player", "playerBehavior", "player-behavior.json")):
            path = original_directory / relative
            self.write(path, self.behavior_paths[name].read_bytes())
            original_inventory.append({"path": relative, "sha256": digest(path)})
        self.prepared["behaviorOracles"] = snapshot_behavior_oracles(
            original_directory, self.directory, self.profile,
            {"stages": copy.deepcopy(stages), "behaviorReports": original_inventory})
        self.batch = {"status": "passed", "profiles": {self.profile: {
            "assembly": self.assembly,
            "sourceDestination": "Assets/Recovered/" + self.assembly,
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
        candidate = export / "RecoveredManaged" / (self.assembly + ".dll")
        self.write(candidate, b"neutral recovered declaration assembly")
        self.prepared["artifacts"].append({"path": candidate.relative_to(self.directory).as_posix(),
                                            "sha256": digest(candidate)})
        self.declaration_stage(self.directory, self.prepared, "recoveredDeclarations", candidate)
        rebuilt = (self.batch_directory / "player/RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed" /
                   (self.assembly + ".dll"))
        self.write(rebuilt, b"neutral rebuilt declaration assembly")
        self.declaration_stage(self.directory, self.prepared, "rebuiltDeclarations", rebuilt)

    @staticmethod
    def write(path, content):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    def declaration_stage(self, directory, prepared, name, candidate):
        oracle = directory / prepared["managedOracles"]["files"]["stripped"]["path"]
        report = {"status": "passed", "differenceCount": 0, "differences": [], "diagnostics": [],
                  "oracle": {"path": str(oracle), "sha256": digest(oracle)},
                  "candidate": {"path": str(candidate), "sha256": digest(candidate), "counts": {"methods": 1}},
                  "stripping": {"lostIdentities": []}}
        path = directory / name / "report.json"
        self.write(path, json.dumps(report).encode())
        prepared["stages"][name] = {"status": "passed", "differenceCount": 0, "counts": report["candidate"]["counts"],
                                     "report": str(path), "reportSha256": digest(path),
                                     "oracleSha256": digest(oracle), "candidateSha256": digest(candidate)}

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
        prepared["batchSourceDirectory"] = str(self.source / ".." / self.assembly)
        with self.assertRaises(ValueError):
            self.check(prepared)

    def test_rejects_relocated_export_even_with_an_unchanged_source_inventory(self):
        record = self.batch["profiles"][self.profile]
        for destination in ("Assets/" + self.assembly, "../outside", "/Assets/Recovered/" + self.assembly):
            with self.subTest(destination=destination):
                record["sourceDestination"] = destination
                with self.assertRaisesRegex(ValueError, "original layout"):
                    self.check()
        record["sourceDestination"] = "Assets/Recovered/" + self.assembly
        del record["sourceDestination"]
        with self.assertRaisesRegex(ValueError, "original layout"):
            self.check()

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

    def test_reauthenticates_reports_and_baseline_after_comparison_subprocesses(self):
        baseline = self.directory.parent / "original"
        path = baseline / "receipt.json"
        self.write(path, b'{"status":"passed"}')
        self.prepared["baselineReceipt"] = {"sha256": digest(path)}
        self.check()
        checked_final_profile(self.directory, self.prepared, self.batch_directory, self.batch, baseline)
        report = self.behavior_paths["playerBehavior"]
        original = report.read_bytes()
        report.write_bytes(original + b"\n")
        with self.assertRaisesRegex(ValueError, "changed since"):
            checked_final_profile(self.directory, self.prepared, self.batch_directory, self.batch, baseline)
        report.write_bytes(original)
        path.write_bytes(path.read_bytes() + b"\n")
        with self.assertRaisesRegex(ValueError, "baseline receipt changed"):
            checked_final_profile(self.directory, self.prepared, self.batch_directory, self.batch, baseline)

    def test_requires_exact_recovered_report_inventory(self):
        record = self.batch["profiles"][self.profile]
        original = copy.deepcopy(record["behaviorReports"])
        for invalid in (None, [], original[:1], [original[0]] * 2,
                        original + [{"path": "extra.json", "sha256": "invalid"}],
                        [{"path": [], "sha256": "invalid"}, original[1]]):
            with self.subTest(inventory=invalid):
                record["behaviorReports"] = invalid
                with self.assertRaisesRegex(ValueError, "inventory"):
                    self.check()

    def test_independent_projection_cannot_replace_exact_original_observations(self):
        path = self.behavior_paths["playerBehavior"]
        report = json.loads(path.read_text(encoding="utf-8"))
        report["observations"][2]["value"] = 19
        path.write_text(json.dumps(report), encoding="utf-8")
        self.update_behavior_hash(path)
        # A projected oracle may accept a value it does not claim to recover.
        # The authenticated original snapshot remains the full equality gate.
        with mock.patch("run_roundtrip_batch.verify_behavior", side_effect=lambda _, stage, profile:
                        self.batch["profiles"][profile]["stages"][stage + "Behavior"]):
            with self.assertRaisesRegex(ValueError, "observations differ"):
                self.check()

    def run_isolated_batch(self, late_mutation=None, malformed_rejected=False, second_valid=False):
        """Run the real coordinator with neutral artifacts and fake external processes."""
        repository = self.directory.parent / "repository"
        (repository / "Files").mkdir(parents=True)
        self.write(repository / "global.json", b'{"sdk":{"version":"10.0.107"}}')
        self.write(repository / runner.comparer_snapshot.PROJECT, b"<Project />")
        rejected = "native-boolean-toggle-invocation"
        baselines = {name: repository / (name + "-original") for name in (rejected, self.profile)}
        for baseline in baselines.values():
            self.write(baseline / "receipt.json", b'{"status":"passed"}')
        run_directory = repository / "Files/result"
        calls = []
        preparations = {}

        def prepare_valid(name, target):
            shutil.copytree(self.directory, target)
            prepared = copy.deepcopy(self.prepared)
            assembly = runner.PROFILES[name]["assembly"]
            source = target / "recovered/UnityProject/Assets/Recovered" / self.assembly
            candidate = target / "recovered/UnityProject/RecoveredManaged" / (self.assembly + ".dll")
            if assembly != self.assembly:
                renamed_source = source.with_name(assembly)
                source.rename(renamed_source)
                source = renamed_source
                (source / (self.assembly + ".asmdef")).unlink()
                self.write(source / (assembly + ".asmdef"), json.dumps({"name": assembly}).encode())
                renamed_candidate = candidate.with_name(assembly + ".dll")
                candidate.rename(renamed_candidate)
                candidate = renamed_candidate
                for kind, record in prepared["managedOracles"]["files"].items():
                    old = target / record["path"]
                    renamed = old.with_name(assembly + ".dll")
                    old.rename(renamed)
                    record["path"] = renamed.relative_to(target).as_posix()
                for stage, record in prepared["behaviorOracles"].items():
                    path = target / record["path"]
                    report = json.loads(path.read_text())
                    report.update(profile=name, observations=toggle_observations())
                    self.write(path, json.dumps(report).encode())
                    record.update(sha256=digest(path), gate=verify_behavior(path, stage, name))
                    prepared["stages"]["original"][stage + "Behavior"] = record["gate"]
            prepared.update(profile=name, scope=assembly, batchSourceDirectory=str(source),
                            baselineReceipt={"sha256": digest(baselines[name] / "receipt.json")},
                            referenceConfiguration={"declarations": []})
            prepared["artifacts"] = [{"path": p.relative_to(target).as_posix(), "sha256": digest(p)}
                                     for p in sorted((target / "recovered").rglob("*")) if p.is_file()]
            self.declaration_stage(target, prepared, "recoveredDeclarations", candidate)
            del prepared["stages"]["rebuiltDeclarations"]
            preparations[name] = prepared
            return prepared

        def external(command, environment, log, timeout, cwd=None):
            if len(command) > 1 and command[1] == "build":
                output = (repository / runner.comparer_snapshot.PROJECT).parent / "bin/Release/net10.0"
                self.write(output / "DeclarationComparer.dll", b"neutral comparer runtime")
                self.write(output / "DeclarationComparer.deps.json", b"{}")
                self.write(output / "DeclarationComparer.runtimeconfig.json",
                           b'{"runtimeOptions":{"tfm":"net10.0","framework":'
                           b'{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
            elif "--defer-unity" in command:
                name = command[command.index("--profile") + 1]
                target = Path(command[command.index("--run-dir") + 1])
                if name == self.profile or second_valid:
                    prepared = prepare_valid(name, target)
                else:
                    prepared = {"status": "awaiting-unity-batch", "profile": name,
                                "scope": "ChangedAssemblyBoundary", "stages": {"original": {"status": "retained"}}}
                self.write(target / "roundtrip.json", json.dumps(prepared).encode())
            elif "--fixtures" in command:
                target = Path(command[command.index("--run-dir") + 1])
                shutil.copytree(self.batch_directory, target)
                batch = copy.deepcopy(self.batch)
                if second_valid:
                    prepared = preparations[rejected]
                    assembly = prepared["scope"]
                    source = Path(prepared["batchSourceDirectory"])
                    shutil.copytree(source, target / "project/Assets/Recovered" / assembly)
                    record = {"assembly": assembly, "sourceDestination": "Assets/Recovered/" + assembly,
                              "sourceFiles": [{"path": p, "sha256": h} for p, h in current_source_files(source).items()],
                              "stages": copy.deepcopy(prepared["stages"]["original"]), "behaviorReports": []}
                    for stage, oracle in prepared["behaviorOracles"].items():
                        path = (target / "project/Reports/batch" / rejected / "editor-behavior.json"
                                if stage == "editor" else target / "player-behavior" / (rejected + ".json"))
                        self.write(path, (run_directory / rejected / oracle["path"]).read_bytes())
                        record["behaviorReports"].append({"path": path.relative_to(target).as_posix(),
                                                          "sha256": digest(path)})
                    batch["profiles"][rejected] = record
                    self.write(target / "player/RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed" /
                               (assembly + ".dll"), b"neutral rebuilt declaration oracle")
                else:
                    batch["profiles"][rejected] = {"assembly": runner.PROFILES[rejected]["assembly"]}
                batch["playerInputs"] = []
                for relative in ("GameAssembly.dll", "RecoveryFixture.exe", "UnityPlayer.dll",
                                 "RecoveryFixture_Data/il2cpp_data/Metadata/global-metadata.dat"):
                    path = target / "player-input" / relative
                    self.write(path, b"neutral native runtime snapshot")
                    batch["playerInputs"].append({"path": relative, "sha256": digest(path)})
                self.write(target / "receipt.json", json.dumps(batch).encode())
                self.write(target / "player/RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed" /
                           (self.assembly + ".dll"), b"neutral rebuilt declaration oracle")
                if malformed_rejected:
                    self.write(run_directory / rejected / "roundtrip.json", b"[]")
            else:
                self.assertTrue(command[1].endswith("DeclarationComparer.dll"))
                calls.append(command)
                output = Path(command[command.index("--output") + 1])
                oracle = Path(command[command.index("--oracle") + 1])
                candidate = Path(command[command.index("--candidate") + 1])
                self.write(output / "report.json", json.dumps({"status": "passed", "differenceCount": 0,
                           "differences": [], "diagnostics": [],
                           "oracle": {"path": str(oracle), "sha256": digest(oracle)},
                           "candidate": {"path": str(candidate), "sha256": digest(candidate), "counts": {"methods": 1}},
                           "stripping": {"lostIdentities": []}}).encode())
                if late_mutation == "receipt":
                    path = run_directory / "rebuilt-batch/receipt.json"
                elif late_mutation == "player":
                    path = run_directory / "rebuilt-batch/player-input/GameAssembly.dll"
                elif late_mutation == "report":
                    path = run_directory / "rebuilt-batch/player-behavior" / (self.profile + ".json")
                elif late_mutation == "manifest":
                    path = run_directory / "shared-comparer" / runner.comparer_snapshot.MANIFEST
                elif late_mutation in ("first-declaration", "first-recovered-declaration") and len(calls) == 2:
                    stage = "rebuiltDeclarations" if late_mutation == "first-declaration" else "recoveredDeclarations"
                    path = run_directory / rejected / stage / "report.json"
                else:
                    path = None
                if path is not None:
                    path.write_bytes(path.read_bytes() + b"\n")
            return {"command": command, "exitCode": 0, "timedOut": False, "seconds": 0.001}

        arguments = ["--editor", "neutral-editor", "--cpp2il", "neutral-tool.dll",
                     "--reference-dir", str(repository), "--run-dir", str(run_directory)]
        for name, path in baselines.items():
            arguments.extend(("--baseline-run", name + "=" + str(path)))
        with mock.patch.object(runner, "ROOT", repository), \
                mock.patch.object(runner, "checked_baseline", return_value={}), \
                mock.patch.object(runner, "run_process", side_effect=external), \
                mock.patch.object(runner.subprocess, "run", return_value=mock.Mock(returncode=0)), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            exit_code = runner.main(arguments)
        expected_status = "passed" if second_valid and late_mutation is None else "failed"
        self.assertTrue(output.getvalue().startswith(expected_status + ": "))
        parent = json.loads((run_directory / "roundtrip.json").read_text())
        rejected_receipt = json.loads((run_directory / rejected / "roundtrip.json").read_text())
        valid_receipt = json.loads((run_directory / self.profile / "roundtrip.json").read_text())
        return exit_code, parent, rejected_receipt, valid_receipt, calls

    def test_rejected_profile_does_not_erase_authenticated_other_profile_or_parent_failure(self):
        code, parent, rejected, valid, calls = self.run_isolated_batch()
        self.assertEqual(code, 1)
        self.assertEqual(parent["status"], "failed")
        self.assertEqual(rejected["status"], "failed")
        self.assertEqual(rejected["error"], "Batch changed the fixture assembly boundary")
        self.assertEqual(rejected["stages"]["original"], {"status": "retained"})
        self.assertEqual(valid["status"], "passed")
        self.assertEqual(valid["stages"]["rebuiltDeclarations"]["differenceCount"], 0)
        self.assertEqual(valid["stages"]["behaviorComparison"]["status"], "passed")
        self.assertEqual(parent["profiles"][self.profile]["status"], "passed")
        self.assertEqual(parent["profiles"][rejected["profile"]]["error"], rejected["error"])
        self.assertEqual(len(calls), 1)
        self.assertIn(self.profile, calls[0][1])

    def test_late_shared_or_successful_profile_mutation_never_publishes_success(self):
        for mutation in ("receipt", "player", "manifest", "report"):
            with self.subTest(mutation=mutation):
                # Each CLI invocation requires a fresh ignored output scope.
                repository = self.directory.parent / "repository"
                if repository.exists():
                    shutil.rmtree(repository)
                code, parent, rejected, valid, calls = self.run_isolated_batch(late_mutation=mutation)
                self.assertEqual(code, 1)
                self.assertEqual(parent["status"], "failed")
                self.assertEqual(rejected["status"], "failed")
                self.assertEqual(valid["status"], "awaiting-unity-batch")
                self.assertFalse(any(record["status"] == "passed" for record in parent["profiles"].values()))
                self.assertEqual(len(calls), 1)

    def test_nonobject_profile_evidence_rejects_explicitly_while_other_profile_completes(self):
        code, parent, rejected, valid, calls = self.run_isolated_batch(malformed_rejected=True)
        self.assertEqual(code, 1)
        self.assertEqual(parent["status"], "failed")
        self.assertEqual(rejected["status"], "failed")
        self.assertIn("profile differs", rejected["error"])
        self.assertEqual(valid["status"], "passed")
        self.assertEqual(len(calls), 1)

    def test_second_valid_profile_cannot_change_an_earlier_declaration_report_before_publication(self):
        for mutation in (None, "first-declaration", "first-recovered-declaration"):
            with self.subTest(mutation=mutation):
                repository = self.directory.parent / "repository"
                if repository.exists():
                    shutil.rmtree(repository)
                code, parent, first, second, calls = self.run_isolated_batch(
                    late_mutation=mutation, second_valid=True)
                self.assertEqual(len(calls), 2)
                if mutation is None:
                    self.assertEqual(code, 0)
                    self.assertEqual(parent["status"], "passed")
                    self.assertEqual(first["status"], "passed")
                    self.assertEqual(second["status"], "passed")
                else:
                    self.assertEqual(code, 1)
                    self.assertEqual(parent["status"], "failed")
                    self.assertIn("Declaration comparison report changed", parent["error"])
                    self.assertEqual(first["status"], "awaiting-unity-batch")
                    self.assertEqual(second["status"], "awaiting-unity-batch")
                    self.assertFalse(any(p["status"] == "passed" for p in parent["profiles"].values()))

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
                 self.directory / "validation-oracles/stripped" / (self.assembly + ".dll"),
                 self.directory / "validation-oracles/behavior/player.json"]
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
