"""Independent bit-pattern and null-failure oracle for a nested Single read."""

import json


def observations():
    values = (
        ("positive", 0x3F800000),
        ("negative", -1071644672),
        ("positive-zero", 0),
        ("negative-zero", -2147483648),
        ("positive-infinity", 0x7F800000),
        ("negative-infinity", -8388608),
        ("nan-payload", 0x7FC01234),
    )
    rows = []
    for kind, bits in values:
        rows.append({"kind": kind, "resultBits": bits, "exception": "none",
                     "sameChild": True, "ownerBefore": -53, "ownerAfter": 59,
                     "childBefore": -37, "childAfter": 41,
                     "childLevelBits": bits})
    rows.append({"kind": "child-null", "resultBits": None,
                 "exception": "System.NullReferenceException", "sameChild": True,
                 "ownerBefore": -53, "ownerAfter": 59,
                 "childBefore": None, "childAfter": None,
                 "childLevelBits": None})
    rows.append({"kind": "owner-null", "resultBits": None,
                 "exception": "System.NullReferenceException", "sameChild": None,
                 "ownerBefore": None, "ownerAfter": None,
                 "childBefore": None, "childAfter": None,
                 "childLevelBits": None})
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "nested-single-getter" or
            report.get("platform") != platform):
        raise ValueError("Nested Single getter report has wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Nested Single getter differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "profile": "nested-single-getter",
            "scope": "Single bits, both null exits, alias identity and unchanged neighbors"}
