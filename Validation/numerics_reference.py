"""Independent observations for a target-provided framework reference."""

import json


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "numerics-reference"):
        raise ValueError("Framework-reference report has the wrong version, stage or profile")
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if expected_platform is None or report.get("platform") != expected_platform:
        raise ValueError("Framework-reference observations require the expected Windows stage")
    expected = [
        {"input": -17, "result": -16},
        {"input": 0, "result": 1},
        {"input": 41, "result": 42},
        {"fieldType": "System.Numerics.BigInteger", "fieldAssembly": "System.Numerics", "zeroRoundTrip": True},
    ]
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Framework-reference behavior or assembly identity differs")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": report["platform"], "profile": "numerics-reference",
            "scope": "target-provided framework field type and bounded arithmetic"}
