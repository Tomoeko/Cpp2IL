"""Independent finite oracle for explicit generic interface dispatch."""

import json


def _call(kind, active_before, active_after, key_before, key_after,
          other_before, other_key, exception="none"):
    return {
        "kind": kind, "exception": exception,
        "resultIsNull": True, "resultType": None,
        "activeCallsBefore": active_before, "activeCallsAfter": active_after,
        "activeKeyBefore": key_before, "activeKeyAfter": key_after,
        "otherCallsBefore": other_before, "otherCallsAfter": other_before,
        "otherKeyBefore": other_key, "otherKeyAfter": other_key,
        "activeNeighborSame": True, "otherNeighborSame": True,
    }


def observations():
    return [
        {"kind": "declarations", "payloadReturnExact": True,
         "textReturnExact": True, "payloadExplicit": True,
         "textExplicit": True, "targetsDistinct": True,
         "genericBaseDefinition": True, "nongenericBaseOverload": True},
        {"kind": "initial", "leftCalls": 0, "leftLastKey": 0,
         "rightCalls": 0, "rightLastKey": 0, "leftAliasesSame": True,
         "ownersDistinct": True, "leftNeighborSame": True,
         "rightNeighborSame": True},
        _call("left-payload", 0, 1, 0, -7, 0, 0),
        _call("right-payload", 0, 1, 0, -7, 1, -7),
        _call("left-text", 1, 2, -7, 17, 1, -7),
        _call("left-payload-repeat", 2, 3, 17, -7, 1, -7),
        _call("left-nongeneric", 3, 4, -7, 31, 1, -7),
        _call("null-payload", 4, 4, 31, 31, 1, -7,
              "System.NullReferenceException"),
        _call("null-text", 4, 4, 31, 31, 1, -7,
              "System.NullReferenceException"),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "generic-dispatch" or
            report.get("platform") != platform):
        raise ValueError("Generic dispatch report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Generic dispatch behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "generic-dispatch",
            "scope": "explicit interface dispatch, declaration identity, signed keys, receiver aliases, side effects and null calls"}
