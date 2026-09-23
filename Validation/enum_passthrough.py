"""Independent signed-I4 oracle for the guarded same-enum parameter call."""

import json


def observations():
    expected = [{"kind": "constructor", "receiverCreated": True,
                 "ownerCreated": True, "lastAfter": 0, "neighborAfter": 123456789}]
    for kind, value in (("minimum", -(1 << 31)), ("negative", -17),
                        ("zero", 0), ("positive", 17), ("maximum", (1 << 31) - 1)):
        expected.append({"kind": kind, "value": value, "exception": "none",
                         "lastAfter": value ^ 0x55555555,
                         "neighborAfter": 123456789})
    expected.append({"kind": "null-receiver", "value": -1,
                     "exception": "System.NullReferenceException", "lastAfter": None,
                     "neighborAfter": None})
    expected.append({"kind": "null-owner", "value": -1,
                     "exception": "System.NullReferenceException", "lastAfter": 41,
                     "neighborAfter": 123456789})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "enum-passthrough"):
        raise ValueError("Enum passthrough report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Enum passthrough native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Enum passthrough behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "enum-passthrough",
            "scope": "signed-I4 enum argument transfer, stored value, null owner and null receiver"}
