import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import native_framework_reference_transport as oracle


class NativeFrameworkReferenceTransportOracleTests(unittest.TestCase):
    def verify_rows(self, rows, stage):
        with TemporaryDirectory() as folder:
            path = Path(folder) / "observations.json"
            path.write_text(json.dumps({"unityVersion": "2021.3.35f1",
                                       "platform": "WindowsEditor" if stage == "editor" else "WindowsPlayer",
                                       "stage": stage, "profile": "native-framework-reference-transport",
                                       "observations": rows}))
            return oracle.verify(path, stage, "2021.3.35f1")

    def test_compiler_and_player_identities_are_not_interchangeable(self):
        for stage, other in (("editor", "player"), ("player", "editor")):
            rows = oracle.observations(stage)
            rows[1]["token"] = oracle.observations(other)[1]["token"]
            with self.subTest(stage=stage), self.assertRaises(ValueError):
                self.verify_rows(rows, stage)

    def test_wrong_framework_identity_or_signature_is_rejected(self):
        for key, value in (("name", "System.Neutral.Package"), ("version", "4.0.0.1"),
                           ("signatureBound", False)):
            rows = oracle.observations("player")
            rows[2][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_rows(rows, "player")

    def test_changed_return_identity_or_field_is_rejected(self):
        for key in ("returnedIncoming", "matchesFirst", "fieldUnchanged"):
            rows = oracle.observations("player")
            rows[10][key] = False
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_rows(rows, "player")

    def test_declaration_and_missing_scope_are_rejected(self):
        rows = oracle.observations("editor")
        rows[0]["methods"][1]["static"] = False
        with self.assertRaises(ValueError):
            self.verify_rows(rows, "editor")
        with self.assertRaises(ValueError):
            self.verify_rows(oracle.observations("editor")[:-1], "editor")

    def test_framework_setup_behavior_is_not_assumed_from_assembly_names(self):
        rows = oracle.observations("player")
        rows[-1]["xmlValue"] = "-5"
        with self.assertRaises(ValueError):
            self.verify_rows(rows, "player")

    def test_stage_must_be_explicit(self):
        with self.assertRaises(ValueError):
            oracle.observations("unknown")
