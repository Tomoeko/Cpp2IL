import json
from pathlib import Path
import tempfile
import unittest

import application_parent_cctor_array_argument as parent
import owner_cctor_array_argument as owner
import element_cctor_array_argument as element


class ArrayArgumentStaticInitializationOracleTests(unittest.TestCase):
    def report(self, oracle):
        return {"unityVersion": "2021.3.35f1", "platform": "WindowsPlayer", "stage": "player",
                "profile": oracle.PROFILE, "observations": oracle.observations()}

    def check(self, oracle, report, accepts=False):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            if accepts:
                result = oracle.verify(path, "player", "2021.3.35f1")
                self.assertEqual((result["methods"], result["observations"]), (oracle.METHODS, 38))
            else:
                with self.assertRaises(ValueError): oracle.verify(path, "player", "2021.3.35f1")

    @staticmethod
    def row(report, kind):
        return next(row for row in report["observations"] if row["kind"] == kind)

    def test_all_three_original_scopes_are_accepted(self):
        for oracle in (parent, owner, element):
            with self.subTest(profile=oracle.PROFILE): self.check(oracle, self.report(oracle), True)

    def test_initial_null_receiver_cannot_trigger_the_observed_cctor_effect(self):
        for oracle in (parent, owner, element):
            with self.subTest(profile=oracle.PROFILE):
                report = self.report(oracle)
                self.row(report, "null-before-creation")["eventsAfter"] = 1
                self.check(oracle, report)

    def test_parent_and_owner_do_not_initialize_when_only_payload_is_created(self):
        for oracle in (parent, owner):
            with self.subTest(profile=oracle.PROFILE):
                report = self.report(oracle)
                self.row(report, "payload-first-created")["events"] = 1
                self.check(oracle, report)

    def test_element_initializer_runs_before_its_first_instance(self):
        report = self.report(element)
        self.row(report, "payload-first-created")["events"] = 0
        self.check(element, report)

    def test_repeated_creation_and_calls_do_not_repeat_static_initialization(self):
        for oracle in (parent, owner, element):
            with self.subTest(profile=oracle.PROFILE):
                report = self.report(oracle)
                self.row(report, "cell-repeat-created")["events"] = 2
                self.check(oracle, report)
                report = self.report(oracle)
                self.row(report, "repeat-first")["eventsAfter"] = 2
                self.check(oracle, report)

    def test_static_constructor_and_probe_cannot_be_removed_from_declarations(self):
        report = self.report(parent)
        self.row(report, "declarations")["methods"] -= 1
        self.check(parent, report)
        report = self.report(owner)
        self.row(report, "declarations")["fieldStatic"]["Probe.Events"] = False
        self.check(owner, report)

    def test_bounds_failure_keeps_initialized_state_without_callee_effects(self):
        report = self.report(element)
        self.row(report, "array-empty")["after"]["first"]["calls"] += 1
        self.check(element, report)
        report = self.report(element)
        self.row(report, "array-empty")["eventsAfter"] = 0
        self.check(element, report)

    def test_lifecycle_cannot_be_omitted_reordered_or_untyped(self):
        report = self.report(owner)
        report["observations"].pop(2)
        self.check(owner, report)
        report = self.report(owner)
        rows = report["observations"]
        rows[2], rows[3] = rows[3], rows[2]
        self.check(owner, report)
        report = self.report(owner)
        self.row(report, "probe-before-creation")["events"] = False
        self.check(owner, report)


if __name__ == "__main__":
    unittest.main()
