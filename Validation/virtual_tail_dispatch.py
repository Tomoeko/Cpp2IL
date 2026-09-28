"""Independent behavior oracle for a zero-argument virtual tail dispatch."""

import json


def observations():
    return [
        {"kind": "base:first", "exception": "none", "baseMarker": 11, "derivedMarker": 7},
        {"kind": "derived:first", "exception": "none", "baseMarker": 11, "derivedMarker": 29},
        {"kind": "base:again", "exception": "none", "baseMarker": 11, "derivedMarker": 2},
        {"kind": "derived:again", "exception": "none", "baseMarker": 11, "derivedMarker": 29},
        {"kind": "null", "exception": "System.NullReferenceException", "baseMarker": 11,
         "derivedMarker": 29},
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "virtual-tail-dispatch" or
            report.get("platform") != platform or
            report.get("observations") != observations()):
        raise ValueError("Virtual tail dispatch behavior differs from its independent oracle")
    return {"status": "passed", "observations": 5, "methods": 5,
            "platform": platform, "profile": "virtual-tail-dispatch",
            "scope": "base and override field effects through a virtual tail, repeated calls and null receiver"}
