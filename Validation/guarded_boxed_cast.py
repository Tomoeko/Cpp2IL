"""Independent finite oracle for a guarded boxed Int32 comparison."""

import json


def observations():
    return [
        {"kind": "equal", "result": True, "exception": "none", "valueIsNull": False},
        {"kind": "different", "result": False, "exception": "none", "valueIsNull": False},
        {"kind": "repeat", "result": True, "exception": "none", "valueIsNull": False},
        {"kind": "other-value-type", "result": False, "exception": "none", "valueIsNull": False},
        {"kind": "other-reference-type", "result": False, "exception": "none", "valueIsNull": False},
        {"kind": "null", "result": False, "exception": "none", "valueIsNull": True},
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "guarded-boxed-cast" or
            report.get("platform") != platform or
            report.get("observations") != observations()):
        raise ValueError("Guarded boxed cast behavior differs from the independent oracle")
    return {"status": "passed", "observations": 6, "methods": 1,
            "platform": platform, "profile": "guarded-boxed-cast",
            "scope": "Exact boxed Int32 equality and null or incompatible inputs"}
