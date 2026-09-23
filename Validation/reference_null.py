"""Independent behavior oracle for managed reference/null comparisons."""

import json


def observations():
    return [
        {"kind": "both-null", "classIsNull": True, "arrayHasValue": False,
         "arrayLength": None, "neighbor": None},
        {"kind": "empty-array", "classIsNull": False, "arrayHasValue": True,
         "arrayLength": 0, "neighbor": 23},
        {"kind": "nonempty-array", "classIsNull": False, "arrayHasValue": True,
         "arrayLength": 3, "neighbor": 23},
        {"kind": "null-array", "classIsNull": False, "arrayHasValue": False,
         "arrayLength": None, "neighbor": 23},
        {"kind": "null-class", "classIsNull": True, "arrayHasValue": True,
         "arrayLength": 1, "neighbor": None},
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "reference-null"):
        raise ValueError("Reference/null report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Reference/null native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Reference/null behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "profile": "reference-null",
            "scope": "class and array parameter null comparisons with unchanged neighboring state"}
