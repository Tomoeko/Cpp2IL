"""Independent behavior oracle for the inherited string-literal call control."""

import json


def observations():
    expected = [{"kind": "constructors", "nodeCreated": True,
                 "resolverCreated": True, "derivedResolverCreated": True}]

    for kind, result, text in (
        ("first", "rrr|suffix", "rrr"),
        ("repeat", "rrr|suffix", "rrr"),
        ("null-text", "|suffix", None),
        ("empty-text", "|suffix", ""),
        ("unicode-text", "\u03A9|suffix", "\u03A9"),
    ):
        expected.append({"kind": kind, "result": result, "exception": "none",
                         "sameNode": True, "textAfter": text,
                         "nodeMarkerAfter": -(1 << 31),
                         "derivedMarkerAfter": (1 << 31) - 1,
                         "neighborAfter": -41, "extraAfter": None})

    expected.append({"kind": "null-node", "result": None,
                     "exception": "System.NullReferenceException", "sameNode": True,
                     "textAfter": None, "nodeMarkerAfter": None,
                     "derivedMarkerAfter": None, "neighborAfter": -41,
                     "extraAfter": None})
    expected.append({"kind": "null-owner", "result": None,
                     "exception": "System.NullReferenceException", "sameNode": False,
                     "textAfter": None, "nodeMarkerAfter": None,
                     "derivedMarkerAfter": None, "neighborAfter": None,
                     "extraAfter": None})
    expected.append({"kind": "virtual-dispatch", "result": "dispatch|suffix",
                     "exception": "none", "sameNode": True,
                     "textAfter": "dispatch", "nodeMarkerAfter": 11,
                     "derivedMarkerAfter": 13, "neighborAfter": 17,
                     "extraAfter": 43})
    expected.append({"kind": "neighbors", "nodeMarker": -(1 << 31),
                     "derivedNodeMarker": (1 << 31) - 1,
                     "resolverNeighbor": -41, "sameNode": True,
                     "dispatchNodeMarker": 11, "dispatchDerivedMarker": 13,
                     "dispatchNeighbor": 17, "dispatchExtra": 43,
                     "sameDispatchNode": True})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "literal-concat" or
            report.get("platform") != platform):
        raise ValueError("Literal-concat report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Literal-concat behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 7,
            "platform": platform, "profile": "literal-concat",
            "scope": "inherited string field, literal concatenation, nulls, dispatch and unchanged neighbors"}
