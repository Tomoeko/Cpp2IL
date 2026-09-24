"""Independent observations for explicit Unity external-reference kinds."""

import json


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "external-references"):
        raise ValueError("External-reference report has the wrong version, stage or profile")
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if expected_platform is None or report.get("platform") != expected_platform:
        raise ValueError("External-reference observations require the expected Windows stage")
    expected = [
        {"input": -17, "result": -16},
        {"input": 0, "result": 1},
        {"input": 41, "result": 42},
        {"packageAssembly": "Neutral.Package", "pluginAssembly": "Neutral.Plugin",
         "packageIdentity": True, "pluginIdentity": True},
    ]
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("External-reference behavior or assembly identities differ")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": report["platform"], "profile": "external-references",
            "scope": "explicit embedded asmdef and precompiled plug-in field types with bounded arithmetic"}
