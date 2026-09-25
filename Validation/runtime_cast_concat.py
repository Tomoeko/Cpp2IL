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


def _property_lookup(label, current_type, text, neighbor, result_type=None,
                     identity=False, exception="none", owner=True):
    return {"kind": "property-lookup:" + label, "resultType": result_type,
            "sameResultAsCurrent": identity, "exception": exception,
            "sameCurrentAfter": owner, "currentType": current_type,
            "textAfter": text, "neighborAfter": neighbor}


def _property_compose(label, current_type, text, neighbor, result=None,
                      exception="none", owner=True):
    return {"kind": "property-compose:" + label, "result": result,
            "exception": exception, "sameCurrentAfter": owner,
            "currentType": current_type, "textAfter": text,
            "neighborAfter": neighbor}


def _property_observations():
    return [
        {"kind": "property-control", "sameTwinCurrent": True,
         "twinNeighbor": 103},
        _property_lookup("null-current", None, None, 101),
        _property_compose("null-current", None, None, 101,
                          exception="System.NullReferenceException"),
        _property_lookup("incompatible", "BaseNode", "plain-property", 101),
        _property_compose("incompatible", "BaseNode", "plain-property", 101,
                          exception="System.NullReferenceException"),
        _property_lookup("exact", "DerivedNode", "owned", 101,
                         "DerivedNode", True),
        _property_compose("exact", "DerivedNode", "owned", 101, "owned|tag"),
        _property_compose("null-text", "DerivedNode", None, 101, "|tag"),
        _property_lookup("subclass", "FurtherNode", "sub", 101,
                         "FurtherNode", True),
        _property_compose("subclass", "FurtherNode", "sub", 101, "sub|tag"),
        _property_lookup("null-owner", None, None, None,
                         exception="System.NullReferenceException", owner=False),
        _property_compose("null-owner", None, None, None,
                          exception="System.NullReferenceException", owner=False),
        {"kind": "property-neighbors", "plainMarker": 71,
         "exactMarker": -73, "exactDetail": 79,
         "furtherMarker": 83, "furtherDetail": 89, "furtherExtra": 97,
         "ownerNeighbor": 101, "twinNeighbor": 103,
         "sameTwinCurrent": True},
    ]


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
    ] + _property_observations()


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
    return {"status": "passed", "observations": len(expected), "methods": 21,
            "platform": platform, "profile": "runtime-cast-concat",
            "scope": "literal and TypeInfo loading, inherited property getter, cast identity, null and incompatible references, aliases and neighbors"}
