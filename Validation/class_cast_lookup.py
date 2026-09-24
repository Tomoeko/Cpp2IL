"""Independent finite oracle for the class-cast lookup control."""

import json


def _row(label, current_type, text, marker, detail, extra, neighbor,
         result_type=None, identity=False, exception="none", owner=True):
    return {"kind": "lookup:" + label, "resultType": result_type,
            "sameResultAsCurrent": identity, "exception": exception,
            "sameCurrentAfter": owner, "currentType": current_type,
            "labelAfter": text, "markerAfter": marker,
            "detailAfter": detail, "extraAfter": extra,
            "neighborAfter": neighbor}


def observations():
    return [
        {"kind": "constructors", "plain": True, "derived": True,
         "further": True, "owner": True, "alias": True},
        _row("null", None, None, None, None, None, 43),
        _row("incompatible", "BaseNode", "plain", 11, None, None, 43),
        _row("exact", "DerivedNode", "dd", -17, 23, None, 43,
             "DerivedNode", True),
        _row("repeat", "DerivedNode", "dd", -17, 23, None, 43,
             "DerivedNode", True),
        _row("subclass", "FurtherNode", "further", 31, 37, 41, 43,
             "FurtherNode", True),
        _row("alias-first", "DerivedNode", "dd", -17, 23, None, 43,
             "DerivedNode", True),
        _row("alias-second", "DerivedNode", "dd", -17, 23, None, -59,
             "DerivedNode", True),
        _row("changed-first", "DerivedNode", "changed", -17, 23, None, 43,
             "DerivedNode", True),
        _row("changed-second", "DerivedNode", "changed", -17, 23, None, -59,
             "DerivedNode", True),
        _row("null-owner", None, None, None, None, None, None,
             exception="System.NullReferenceException", owner=False),
        {"kind": "neighbors", "plainMarker": 11, "derivedMarker": -17,
         "derivedDetail": 23, "furtherMarker": 31, "furtherDetail": 37,
         "furtherExtra": 41, "ownerNeighbor": 43, "aliasNeighbor": -59,
         "sameAlias": True},
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "class-cast-lookup" or
            report.get("platform") != platform):
        raise ValueError("Class-cast report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Class-cast behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 5,
            "platform": platform, "profile": "class-cast-lookup",
            "scope": "null and incompatible references, class-cast identity, aliases and unchanged fields"}
