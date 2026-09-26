"""Independent oracle for fixed-index reference array reads and failures."""

import json


def observations():
    rows = []
    for length in range(6):
        for index in range(2, 5):
            valid = index < length
            rows.append({"kind": f"length-{length}", "index": index,
                         "resultId": index + 11 if valid else None,
                         "exception": "none" if valid else "System.IndexOutOfRangeException",
                         "sameElement": True if valid else None,
                         "sameArray": True, "ownerBefore": -53,
                         "ownerAfter": 59, "itemsUnchanged": True})
    for index in range(2, 5):
        rows.append({"kind": "element-null", "index": index, "resultId": None,
                     "exception": "none", "sameElement": True,
                     "sameArray": True, "ownerBefore": -53,
                     "ownerAfter": 59, "itemsUnchanged": True})
    for index in range(2, 5):
        rows.append({"kind": "array-null", "index": index, "resultId": None,
                     "exception": "System.NullReferenceException", "sameElement": None,
                     "sameArray": True,
                     "ownerBefore": -53, "ownerAfter": 59,
                     "itemsUnchanged": None})
        rows.append({"kind": "owner-null", "index": index, "resultId": None,
                     "exception": "System.NullReferenceException", "sameElement": None,
                     "sameArray": None,
                     "ownerBefore": None, "ownerAfter": None,
                     "itemsUnchanged": None})
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "fixed-reference-array" or
            report.get("platform") != platform):
        raise ValueError("Fixed reference array report has wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Fixed reference array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 5,
            "platform": report["platform"], "profile": "fixed-reference-array",
            "scope": "fixed indices, bounds and null exits, identity and unchanged neighbors"}
