"""Independent bit-pattern and effect oracle for a forwarded Single store."""

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
    result = []
    for kind, bits in values:
        result.append({"kind": kind, "beforeBits": 0x3FA00000, "afterBits": bits,
                       "exception": "none", "sameTarget": True,
                       "ownerBefore": -53, "ownerAfter": 59,
                       "targetBefore": -37, "targetAfter": 41})
    result.append({"kind": "target-null", "beforeBits": None, "afterBits": None,
                   "exception": "System.NullReferenceException", "sameTarget": True,
                   "ownerBefore": -53, "ownerAfter": 59,
                   "targetBefore": None, "targetAfter": None})
    result.append({"kind": "owner-null", "beforeBits": None, "afterBits": None,
                   "exception": "System.NullReferenceException", "sameTarget": None,
                   "ownerBefore": None, "ownerAfter": None,
                   "targetBefore": None, "targetAfter": None})
    return result


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "float-forward-store" or
            stage == "player" and report.get("platform") != "WindowsPlayer"):
        raise ValueError("Float-forward report has the wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Float-forward behavior differs from the independent bit-pattern oracle")
    return {"status": "passed", "observations": len(expected), "methods": 5,
            "platform": report["platform"], "profile": "float-forward-store",
            "scope": "forwarded Single bits, both null exits, alias identity, and unchanged neighbors"}
