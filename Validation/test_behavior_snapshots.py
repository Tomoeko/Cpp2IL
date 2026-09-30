"""Strict observed-bit comparison alongside independent behavior projections."""

import copy
import json
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest import mock

import run_roundtrip as roundtrip
import run_fixture as fixture
from run_fixture import VERSION, verify_behavior
from static_scalar_setter import observations


class DeclarationReportAuthenticationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.directory = self.root / "recovery"
        self.rebuilt = self.root / "rebuilt"
        assembly = "NeutralFixture"
        self.receipt = {"scope": assembly, "managedOracles": {"files": {}}, "stages": {}}
        for kind in ("stripped", "unstripped"):
            path = self.directory / "validation-oracles" / kind / (assembly + ".dll")
            self.write(path, b"neutral original declaration assembly")
            self.receipt["managedOracles"]["files"][kind] = {
                "path": path.relative_to(self.directory).as_posix(), "sha256": roundtrip.digest(path)}
        self.candidates = {
            "recoveredDeclarations": self.directory / "recovered/UnityProject/RecoveredManaged" / (assembly + ".dll"),
            "rebuiltDeclarations": self.rebuilt / "player/RecoveryFixture_BackUpThisFolder_ButDontShipItWithYourGame/Managed" /
                                   (assembly + ".dll"),
        }
        self.oracle = self.directory / self.receipt["managedOracles"]["files"]["stripped"]["path"]
        for name, candidate in self.candidates.items():
            self.write(candidate, b"neutral projected declaration assembly")
            report = {"status": "passed", "differenceCount": 0, "differences": [], "diagnostics": [],
                      "oracle": {"path": str(self.oracle), "sha256": roundtrip.digest(self.oracle)},
                      "candidate": {"path": str(candidate), "sha256": roundtrip.digest(candidate), "counts": {"methods": 2}}}
            path = self.directory / name / "report.json"
            self.write(path, json.dumps(report).encode())
            self.receipt["stages"][name] = {
                "status": "passed", "differenceCount": 0, "counts": report["candidate"]["counts"],
                "report": str(path), "reportSha256": roundtrip.digest(path),
                "oracleSha256": roundtrip.digest(self.oracle), "candidateSha256": roundtrip.digest(candidate)}

    @staticmethod
    def write(path, content):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    def check(self):
        roundtrip.checked_declaration_stages(self.directory, self.receipt, self.rebuilt)

    def test_authenticates_both_single_run_declaration_stages(self):
        self.check()

    def test_rejects_reports_changed_deleted_or_linked_after_comparison(self):
        for name in self.candidates:
            path = self.directory / name / "report.json"
            original = path.read_bytes()
            with self.subTest(stage=name, mutation="bytes"):
                path.write_bytes(original + b"\n")
                with self.assertRaisesRegex(ValueError, "changed since"):
                    self.check()
            path.write_bytes(original)
            with self.subTest(stage=name, mutation="missing"):
                path.unlink()
                with self.assertRaises(ValueError):
                    self.check()
            outside = self.root / (name + ".json")
            outside.write_bytes(original)
            with self.subTest(stage=name, mutation="link"):
                path.symlink_to(outside)
                with self.assertRaises(ValueError):
                    self.check()
            path.unlink()
            path.write_bytes(original)

    def test_rebinds_candidate_bytes_and_rejects_false_report_identities_or_results(self):
        for name, candidate in self.candidates.items():
            path = self.directory / name / "report.json"
            stage = self.receipt["stages"][name]
            original = path.read_bytes()
            candidate.write_bytes(candidate.read_bytes() + b"changed")
            with self.assertRaisesRegex(ValueError, "candidate changed"):
                self.check()
            candidate.write_bytes(b"neutral projected declaration assembly")
            for mutation in ("identity", "counts", "count-type", "diagnostics", "status", "differenceCount", "nonobject"):
                with self.subTest(stage=name, mutation=mutation):
                    report = json.loads(original)
                    if mutation == "identity":
                        report["oracle"]["path"] = str(self.candidates[name])
                    elif mutation == "counts":
                        report["candidate"]["counts"]["methods"] += 1
                    elif mutation == "count-type":
                        report["candidate"]["counts"]["methods"] = 2.0
                    elif mutation == "diagnostics":
                        report["diagnostics"] = ["unresolved declaration"]
                    elif mutation == "status":
                        report["status"] = "failed"
                    elif mutation == "differenceCount":
                        report["differenceCount"] = False
                    else:
                        report = []
                    path.write_text(json.dumps(report), encoding="utf-8")
                    stage["reportSha256"] = roundtrip.digest(path)
                    with self.assertRaises(ValueError):
                        self.check()
            path.write_bytes(original)
            stage["reportSha256"] = roundtrip.digest(path)

    def test_detects_report_mutation_during_final_parse(self):
        path = self.directory / "recoveredDeclarations/report.json"
        read_text = Path.read_text

        def read_and_change(target, *args, **kwargs):
            text = read_text(target, *args, **kwargs)
            if target == path:
                target.write_bytes(target.read_bytes() + b"\n")
            return text

        with mock.patch.object(Path, "read_text", new=read_and_change):
            with self.assertRaisesRegex(ValueError, "changed during"):
                self.check()


class BehaviorSnapshotTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.original = self.root / "original"
        self.recovery = self.root / "recovery"
        self.profile = "static-scalar-setter"
        self.stages = {}
        self.inventory = []
        for stage, relative in (("editor", "project/Reports/editor-behavior.json"),
                                ("player", "player-behavior.json")):
            path = self.original / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(self.report(stage, observations())), encoding="utf-8")
            self.stages[stage + "Behavior"] = verify_behavior(path, stage, self.profile)
            self.inventory.append({"path": relative, "sha256": roundtrip.digest(path)})
        self.receipt = {"stages": self.stages, "behaviorReports": self.inventory}

    def report(self, stage, rows):
        return {"unityVersion": VERSION, "stage": stage, "profile": self.profile,
                "platform": "WindowsEditor" if stage == "editor" else "WindowsPlayer",
                "observations": rows}

    def snapshot(self):
        return roundtrip.snapshot_behavior_oracles(self.original, self.recovery, self.profile, self.receipt)

    def test_snapshots_survive_original_project_pruning_and_compare_typed_observations(self):
        snapshots = self.snapshot()
        shutil.rmtree(self.original)
        for stage, record in snapshots.items():
            original = self.recovery / record["path"]
            recovered = self.root / (stage + "-recovered.json")
            recovered.write_bytes(original.read_bytes())
            result = roundtrip.compare_behavior_oracle(self.recovery, self.profile, snapshots, stage, recovered)
            self.assertEqual(result["status"], "passed")
            self.assertEqual(result["observations"], len(observations()))

    def test_original_report_inventory_and_bytes_are_authenticated(self):
        for invalid in (None, [], self.inventory[:1], [self.inventory[0]] * 2,
                        [{"path": [], "sha256": "invalid"}, self.inventory[1]]):
            with self.subTest(inventory=invalid):
                receipt = {**self.receipt, "behaviorReports": invalid}
                with self.assertRaises(ValueError):
                    roundtrip.checked_original_behavior(self.original, self.profile, receipt)
        path = self.original / "player-behavior.json"
        path.write_bytes(path.read_bytes() + b"\n")
        with self.assertRaisesRegex(ValueError, "changed since"):
            self.snapshot()

    def test_captures_hash_at_verification_and_rejects_late_changes(self):
        path = self.original / "player-behavior.json"
        gate, record = fixture.verified_behavior_report(self.original, path, "player", self.profile)
        self.assertEqual(gate, self.stages["playerBehavior"])
        fixture.checked_behavior_report_files(self.original, [record], [record["path"]])
        original = path.read_bytes()
        path.write_bytes(original + b"\n")
        with self.assertRaisesRegex(ValueError, "changed since"):
            fixture.checked_behavior_report_files(self.original, [record], [record["path"]])
        path.write_bytes(original)

        def mutate_after_verification(report, stage, profile):
            result = verify_behavior(report, stage, profile)
            report.write_bytes(report.read_bytes() + b"\n")
            return result

        with mock.patch.object(fixture, "verify_behavior", side_effect=mutate_after_verification):
            with self.assertRaisesRegex(ValueError, "changed during"):
                fixture.verified_behavior_report(self.original, path, "player", self.profile)

    def test_snapshot_mutation_missing_stage_and_links_are_rejected(self):
        snapshots = self.snapshot()
        missing = copy.deepcopy(snapshots)
        del missing["editor"]
        with self.assertRaises(ValueError):
            roundtrip.checked_behavior_oracles(self.recovery, self.profile, missing)
        path = self.recovery / snapshots["player"]["path"]
        original = path.read_bytes()
        path.write_bytes(original + b"\n")
        with self.assertRaises(ValueError):
            roundtrip.checked_behavior_oracles(self.recovery, self.profile, snapshots)
        path.unlink()
        outside = self.root / "outside.json"
        outside.write_bytes(original)
        path.symlink_to(outside)
        with self.assertRaises(ValueError):
            roundtrip.checked_behavior_oracles(self.recovery, self.profile, snapshots)

    def test_does_not_write_through_a_linked_oracle_directory(self):
        self.recovery.mkdir()
        outside = self.root / "outside"
        outside.mkdir()
        (self.recovery / "validation-oracles").symlink_to(outside, target_is_directory=True)
        with self.assertRaises(ValueError):
            self.snapshot()
        self.assertEqual(list(outside.iterdir()), [])

    def test_projection_acceptance_cannot_hide_nan_payload_or_json_type_changes(self):
        # A classification-only oracle admits either quiet-NaN payload. Exact
        # original/recovered comparison must still reject a changed observed bit.
        rows = [{"resultBits": "7ff8000000000001", "pending": False, "counter": 0}]
        gate = {"status": "passed", "observations": 1, "methods": 2}
        for stage, relative in (("editor", "project/Reports/editor-behavior.json"),
                                ("player", "player-behavior.json")):
            path = self.original / relative
            path.write_text(json.dumps(self.report(stage, rows)), encoding="utf-8")
        self.receipt = {"stages": {stage + "Behavior": gate for stage in ("editor", "player")},
                        "behaviorReports": [{"path": relative, "sha256": roundtrip.digest(self.original / relative)}
                                            for relative in ("project/Reports/editor-behavior.json",
                                                             "player-behavior.json")]}
        with mock.patch.object(roundtrip, "verify_behavior", return_value=gate):
            snapshots = self.snapshot()
            recovered = self.root / "recovered.json"
            recovered.write_text(json.dumps(self.report("player", rows)), encoding="utf-8")
            self.assertEqual(roundtrip.compare_behavior_oracle(
                self.recovery, self.profile, snapshots, "player", recovered)["status"], "passed")
            for key, value in (("resultBits", "7ff8000000000002"), ("pending", 0), ("counter", False)):
                with self.subTest(changed=key):
                    changed = copy.deepcopy(rows)
                    changed[0][key] = value
                    recovered.write_text(json.dumps(self.report("player", changed)), encoding="utf-8")
                    with self.assertRaisesRegex(ValueError, "observations differ"):
                        roundtrip.compare_behavior_oracle(self.recovery, self.profile, snapshots, "player", recovered)
            report = self.report("editor", rows)
            recovered.write_text(json.dumps(report), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "targets differ"):
                roundtrip.compare_behavior_oracle(self.recovery, self.profile, snapshots, "player", recovered)
            recovered.write_text("[]", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "JSON objects"):
                roundtrip.compare_behavior_oracle(self.recovery, self.profile, snapshots, "player", recovered)


if __name__ == "__main__":
    unittest.main()
