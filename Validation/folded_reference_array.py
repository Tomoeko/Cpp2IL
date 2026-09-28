"""Independent bounded oracle for two linker-folded fixed-array getters."""

import json


def observations():
    rows = []
    for length in range(4):
        for index in range(2):
            for owner in ("A", "B"):
                valid = index < length
                rows.append({
                    "kind": f"length-{length}", "owner": owner, "index": index,
                    "resultId": 11 + index if valid else None,
                    "exception": "none" if valid else "System.IndexOutOfRangeException",
                    "sameElement": True if valid else None,
                    "sameArray": True, "ownerBefore": -53, "ownerAfter": 59,
                    "itemsUnchanged": True,
                })
    for index in range(2):
        for owner in ("A", "B"):
            rows.append({
                "kind": "element-null", "owner": owner, "index": index,
                "resultId": None, "exception": "none", "sameElement": True,
                "sameArray": True, "ownerBefore": -53, "ownerAfter": 59,
                "itemsUnchanged": True,
            })
        for owner in ("A", "B"):
            rows.append({
                "kind": "array-null", "owner": owner, "index": index,
                "resultId": None, "exception": "System.NullReferenceException",
                "sameElement": None, "sameArray": True,
                "ownerBefore": -53, "ownerAfter": 59, "itemsUnchanged": None,
            })
        for owner in ("A", "B"):
            rows.append({
                "kind": "owner-null", "owner": owner, "index": index,
                "resultId": None, "exception": "System.NullReferenceException",
                "sameElement": None, "sameArray": None,
                "ownerBefore": None, "ownerAfter": None, "itemsUnchanged": None,
            })
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "folded-reference-array" or
            report.get("platform") != platform):
        raise ValueError("Folded reference-array report has wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Folded reference-array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 7,
            "platform": report["platform"], "profile": "folded-reference-array",
            "scope": "two aliased getter pairs, ordered owner/array null and bounds exits"}
