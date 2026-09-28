"""Independent field-state oracle for the inert Object-constructor fixture."""

import json


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if platform is None or report.get("unityVersion") != version or \
            report.get("stage") != stage or \
            report.get("profile") != "shared-inert-constructor" or \
            report.get("platform") != platform:
        raise ValueError("Shared constructor behavior header differs")
    expected = [
        {"kind": "first", "first": -13, "second": 257, "third": 8191},
        {"kind": "second", "separate": True, "first": -13,
         "second": 257, "third": 8191},
        {"kind": "mixed", "marker": 7, "counter": -21, "offset": 41},
        {"kind": "mutation", "first": 1000, "secondFirst": -13,
         "marker": 1},
    ]
    if report.get("observations") != expected:
        raise ValueError("Shared constructor field-state observations differ")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "checks": 13, "platform": platform}
