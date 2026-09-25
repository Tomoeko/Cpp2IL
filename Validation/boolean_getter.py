"""Independent Boolean field getter oracle for the exact-target fixture."""

import json


def observations():
    expected = []
    for neighbor in (0, -(1 << 31), (1 << 31) - 1):
        for value in (False, True):
            for owner, before in (("first", value), ("given", value),
                                  ("second", not value)):
                expected.append({
                    "kind": "read", "owner": owner,
                    "valueBefore": before, "neighborBefore": neighbor,
                    "result": before, "valueAfter": before,
                    "neighborAfter": neighbor,
                })
    expected.extend({
        "kind": "null", "owner": owner,
        "exception": "System.NullReferenceException",
    } for owner in ("first", "given", "second"))
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "boolean-getter" or
            report.get("platform") != platform):
        raise ValueError("Boolean getter report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Boolean getter behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 5,
            "platform": report["platform"], "profile": "boolean-getter",
            "scope": "field identity, unchanged neighbors and null receivers"}
