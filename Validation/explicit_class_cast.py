"""Finite independent behavior oracle for an explicit reference cast."""

import json


def observations():
    return [
        {"kind": "null", "sameReference": True, "resultType": "null", "failure": "none"},
        {"kind": "exact", "sameReference": True, "resultType": "System.Exception", "failure": "none"},
        {"kind": "subtype", "sameReference": True, "resultType": "System.InvalidOperationException", "failure": "none"},
        {"kind": "exact-repeat", "sameReference": True, "resultType": "System.Exception", "failure": "none"},
        {"kind": "string", "sameReference": True, "resultType": "null", "failure": "System.InvalidCastException"},
        {"kind": "boxed-int", "sameReference": True, "resultType": "null", "failure": "System.InvalidCastException"},
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "explicit-class-cast" or
            report.get("platform") != platform or
            report.get("observations") != observations()):
        raise ValueError("Explicit reference cast behavior differs from the independent oracle")
    return {"status": "passed", "observations": 6, "methods": 1,
            "platform": platform, "profile": "explicit-class-cast",
            "scope": "Exception reference cast with null, compatible and incompatible inputs"}
