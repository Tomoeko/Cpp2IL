"""Independent oracle for an array-returning call and a null receiver."""

import json


def observations():
    expected = [{"kind": "constructor", "created": True, "callsAfter": 0}]
    for calls, (kind, values) in enumerate((("values", [-(1 << 31), 0, (1 << 31) - 1]),
                                            ("empty", []), ("null-array", None)), start=1):
        expected.append({"kind": kind, "result": values, "sameReference": True,
                         "exception": "none", "callsAfter": calls})
    expected.append({"kind": "overflow", "result": [-(1 << 31), (1 << 31) - 1],
                     "sameReference": True, "exception": "none", "callsAfter": -(1 << 31)})
    for kind in ("null-receiver-values", "null-receiver-null-array"):
        expected.append({"kind": kind, "result": None, "sameReference": False,
                         "exception": "System.NullReferenceException", "callsAfter": None})
    expected.append({"kind": "derived-values", "result": [-(1 << 31), 0, (1 << 31) - 1],
                     "sameReference": True, "exception": "none", "callsAfter": 1})
    expected.append({"kind": "derived-null-receiver", "result": None, "sameReference": False,
                     "exception": "System.NullReferenceException", "callsAfter": None})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "array-call":
        raise ValueError("Array-call report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Array-call native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Array-call behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 5,
            "platform": report["platform"], "profile": "array-call",
            "scope": "array identity and contents across base and derived instance calls with null receivers; not whole-program equivalence"}
