"""Independent finite behavior oracle for the neutral class-cast control."""

import json


def _state(current_type, text, marker, detail, extra, neighbor):
    return {"currentType": current_type, "textAfter": text,
            "markerAfter": marker, "detailAfter": detail,
            "extraAfter": extra, "neighborAfter": neighbor}


def _lookup(label, state, result_type=None, identity=False):
    return {"kind": "lookup:" + label, "resultType": result_type,
            "sameResultAsCurrent": identity, "exception": "none",
            "sameCurrentAfter": True, **state}


def _compose(label, state, result=None, exception="none", owner=True):
    return {"kind": "compose:" + label, "result": result,
            "exception": exception, "sameCurrentAfter": owner, **state}


def observations():
    null = _state(None, None, None, None, None, 43)
    plain = _state("BaseNode", "plain", 11, None, None, 43)
    derived = _state("DerivedNode", "dd", -17, 23, None, 43)
    empty_text = _state("DerivedNode", None, -17, 23, None, 43)
    further = _state("FurtherNode", "further", 31, 37, 41, 43)
    alias_first = _state("DerivedNode", "dd", -17, 23, None, 43)
    alias_second = _state("DerivedNode", "dd", -17, 23, None, -59)
    changed_first = _state("DerivedNode", "changed", -17, 23, None, 43)
    changed_second = _state("DerivedNode", "changed", -17, 23, None, -59)
    dispatch = _state("FurtherNode", "further", 31, 37, 41, 47)
    missing = _state(None, None, None, None, None, None)

    return [
        {"kind": "constructors", "plain": True, "derived": True,
         "further": True, "resolver": True, "alias": True,
         "dispatchOwner": True},
        {"kind": "literal:null", "result": "|tag", "exception": "none"},
        {"kind": "literal:first", "result": "vv|tag", "exception": "none"},
        {"kind": "literal:repeat", "result": "vv|tag", "exception": "none"},
        {"kind": "literal:unicode", "result": "\u03A9|tag", "exception": "none"},
        {"kind": "target-type", "name": "RuntimeCastConcatFixture.DerivedNode",
         "matchesManagedType": True, "sameRepeatedType": True},
        _lookup("null", null),
        _compose("null", null, exception="System.NullReferenceException"),
        _lookup("incompatible", plain),
        _compose("incompatible", plain, exception="System.NullReferenceException"),
        _lookup("exact", derived, "DerivedNode", True),
        _compose("exact", derived, "dd|tag"),
        _compose("repeat", derived, "dd|tag"),
        _compose("null-text", empty_text, "|tag"),
        _lookup("subclass", further, "FurtherNode", True),
        _compose("subclass", further, "further|tag"),
        _lookup("alias-first", alias_first, "DerivedNode", True),
        _lookup("alias-second", alias_second, "DerivedNode", True),
        _compose("alias-first", changed_first, "changed|tag"),
        _compose("alias-second", changed_second, "changed|tag"),
        _compose("virtual-dispatch", dispatch, "further|tag"),
        _compose("null-owner", missing, exception="System.NullReferenceException",
                 owner=False),
        {"kind": "neighbors", "plainMarker": 11, "derivedMarker": -17,
         "derivedDetail": 23, "furtherMarker": 31, "furtherDetail": 37,
         "furtherExtra": 41, "resolverNeighbor": 43, "aliasNeighbor": -59,
         "dispatchNeighbor": 47, "dispatchExtra": 53,
         "sameAlias": True, "sameDispatch": True},
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "runtime-cast-concat" or
            report.get("platform") != platform):
        raise ValueError("Runtime-cast report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Runtime-cast behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 10,
            "platform": platform, "profile": "runtime-cast-concat",
            "scope": "literal and TypeInfo loading, cast identity, null and incompatible references, aliases and neighbors"}
