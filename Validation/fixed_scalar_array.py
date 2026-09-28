"""Independent bounded oracle for fixed-index Int32 array getters."""

import json


def observations():
    rows = []
    for length in range(6):
        for index in (1, 3):
            valid = index < length
            value = -(2**31) if index == 1 else 2**31 - 1
            rows.append({"kind": f"length-{length}", "index": index,
                         "result": value if valid else None,
                         "exception": "none" if valid else "System.IndexOutOfRangeException",
                         "sameArray": True, "ownerBefore": -53,
                         "ownerAfter": 59, "state": 101, "valuesUnchanged": True})
    for kind, exception in (("array-null", "System.NullReferenceException"),
                            ("owner-null", "System.NullReferenceException")):
        for index in (1, 3):
            rows.append({"kind": kind, "index": index, "result": None,
                         "exception": exception,
                         "sameArray": True if kind == "array-null" else None,
                         "ownerBefore": -53 if kind == "array-null" else None,
                         "ownerAfter": 59 if kind == "array-null" else None,
                         "state": 101 if kind == "array-null" else None,
                         "valuesUnchanged": None})
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "fixed-scalar-array" or
            report.get("platform") != platform):
        raise ValueError("Fixed scalar array report has wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Fixed scalar array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "profile": "fixed-scalar-array",
            "scope": "fixed Int32 indices, null and bounds exits, unchanged array and neighboring fields"}
