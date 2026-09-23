import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from run_component_probe import ROOT, verify_report, verify_recovery_origin


def report():
    return {
        "unityVersion": "2021.3.35f1", "platform": "OSXEditor", "apiCompatibility": "NET_Unity_4_8",
        "assembly": "ComponentFixture", "assemblyAttributePreserved": True, "ordinaryTypePreserved": True,
        "scripts": [{"path": "Assets/Fixture/MarkerBehaviour.cs", "class": "ComponentFixture.MarkerBehaviour"},
                    {"path": "Assets/Fixture/DataAsset.cs", "class": "ComponentFixture.DataAsset"}],
        "instances": [
            {"class": "ComponentFixture.MarkerBehaviour", "assembly": "ComponentFixture",
             "scriptPath": "Assets/Fixture/MarkerBehaviour.cs", "scriptClass": "ComponentFixture.MarkerBehaviour",
             "fields": [{"path": "Count", "present": True, "kind": "Integer", "managedType": "System.Int32"},
                        {"path": "caption", "present": True, "kind": "String", "managedType": "System.String"},
                        {"path": "Configuration", "present": True, "kind": "ObjectReference", "managedType": "ComponentFixture.DataAsset"},
                        {"path": "Payload.Value", "present": True, "kind": "Integer", "managedType": "System.Int32"}]},
            {"class": "ComponentFixture.DataAsset", "assembly": "ComponentFixture",
             "scriptPath": "Assets/Fixture/DataAsset.cs", "scriptClass": "ComponentFixture.DataAsset",
             "fields": [{"path": "Value", "present": True, "kind": "Integer", "managedType": "System.Int32"},
                        {"path": "label", "present": True, "kind": "String", "managedType": "System.String"}]},
        ],
    }


class ComponentProbeTests(unittest.TestCase):
    def test_compilation_alone_cannot_pass_script_discovery(self):
        observed = report()
        observed["scripts"] = [{"path": "Assets/Fixture/Recovered.cs", "class": None}]
        self.assertEqual(verify_report(observed, "observe")["status"], "observed")
        with self.assertRaises(ValueError):
            verify_report(observed, "bound")

    def test_missing_or_retyped_serialized_field_is_rejected(self):
        for mutate in (lambda fields: fields.pop(), lambda fields: fields[0].update(kind="Float"),
                       lambda fields: fields[2].update(managedType="UnityEngine.Object")):
            observed = report()
            mutate(observed["instances"][0]["fields"])
            with self.assertRaises(ValueError):
                verify_report(observed, "bound")

    def test_fresh_instance_must_bind_to_the_same_script_asset(self):
        observed = report()
        self.assertEqual(verify_report(observed, "bound")["discoveredComponents"], 2)
        wrong = copy.deepcopy(observed)
        wrong["instances"][0]["scriptPath"] = "Assets/Fixture/DataAsset.cs"
        with self.assertRaises(ValueError):
            verify_report(wrong, "bound")

    def test_player_only_provenance_rejects_tampered_missing_or_extra_copied_sources(self):
        scratch = ROOT / "Files/validation-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as name:
            directory = Path(name)
            relative = "recovered/UnityProject/Assets/Recovered/ComponentFixture/"
            source = directory / relative
            source.mkdir(parents=True)
            copied = directory / "copied"
            copied.mkdir()
            original = b"public class Marker { }\n"
            (copied / "Marker.cs").write_bytes(original)
            files = [{"path": "Marker.cs", "sha256": hashlib.sha256(original).hexdigest()}]
            receipt = directory / "roundtrip.json"
            origin = {"status": "passed", "profile": "components", "scope": "ComponentFixture",
                      "recoveryInputs": "isolated player binary and metadata only",
                      "stages": {"recovery": {"status": "passed", "selectedMethods": 3, "selectedUnresolved": 0}},
                      "artifacts": [{"path": relative + "Marker.cs", "sha256": files[0]["sha256"]}]}
            receipt.write_text(json.dumps(origin))
            self.assertEqual(verify_recovery_origin(source, copied, files, receipt)["copiedSourceFilesVerified"], 1)
            (copied / "Marker.cs").write_text("public class Changed { }")
            with self.assertRaisesRegex(ValueError, "artifact manifest"):
                verify_recovery_origin(source, copied, files, receipt)
            (copied / "Marker.cs").write_bytes(original)
            with self.assertRaisesRegex(ValueError, "artifact manifest"):
                verify_recovery_origin(source, copied, [], receipt)
            (copied / "Extra.cs").write_bytes(original)
            with self.assertRaisesRegex(ValueError, "artifact manifest"):
                verify_recovery_origin(source, copied, files, receipt)
            with self.assertRaisesRegex(ValueError, "artifact manifest"):
                verify_recovery_origin(source, copied, files + [{"path": "Extra.cs"}], receipt)
            origin["status"] = "failed"
            receipt.write_text(json.dumps(origin))
            with self.assertRaisesRegex(ValueError, "passed player-only"):
                verify_recovery_origin(source, copied, files, receipt)


if __name__ == "__main__":
    unittest.main()
