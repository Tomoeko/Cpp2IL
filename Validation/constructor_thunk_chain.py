"""Finite independent oracle for a constructor thunk chain control."""

import json


def observations():
    expected = [{"kind": "declarations", "chainsExact": True,
                 "payloadFieldsExact": True, "leafTypesDistinct": True}]
    for kind in ("first", "second", "third", "reflection"):
        expected.append({"kind": kind, "typeExact": True, "state": 29,
                         "neighborInitiallyNull": True,
                         "untouchedInitiallyNull": True})
    for check in ("receiver-alias", "instances-distinct", "neighbor-alias",
                  "neighbors-independent", "state-preserved", "untouched-default"):
        expected.append({"subject": "identity", "check": check, "result": True})
    return expected


def same_typed_value(actual, expected):
    if type(actual) is not type(expected):
        return False
    if isinstance(expected, dict):
        return actual.keys() == expected.keys() and all(
            same_typed_value(actual[key], value) for key, value in expected.items())
    if isinstance(expected, list):
        return len(actual) == len(expected) and all(
            same_typed_value(left, right) for left, right in zip(actual, expected))
    return actual == expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "constructor-thunk-chain" or
            report.get("platform") != platform):
        raise ValueError("Constructor thunk chain report has the wrong target or stage")
    expected = observations()
    if not same_typed_value(report.get("observations"), expected):
        raise ValueError("Constructor thunk chain behavior differs from the oracle")
    return {"status": "passed", "observations": len(expected), "methods": 7,
            "platform": report["platform"], "profile": "constructor-thunk-chain",
            "scope": "constructor field write, inherited defaults, exact declarations and reference identity"}
