"""Independent behavior oracle for an owner-indexed enum-array getter."""

import json


VALUES = (-7, -(2**31) + 17, 42)


def observations():
    rows = []
    for length in range(4):
        for index in range(-1, 4):
            valid = 0 <= index < length
            for owner in ("A", "B"):
                rows.append({
                    "kind": f"length-{length}", "owner": owner, "index": index,
                    "result": VALUES[index] if valid else None,
                    "exception": "none" if valid else "System.IndexOutOfRangeException",
                    "sameArray": True, "slotUnchanged": True,
                    "before": -53, "spacer": -61 if owner == "B" else None,
                    "after": 59, "valuesUnchanged": True,
                })
    for index in (-1, 0, 3):
        for owner in ("A", "B"):
            rows.append({
                "kind": "array-null", "owner": owner, "index": index,
                "result": None, "exception": "System.NullReferenceException",
                "sameArray": True, "slotUnchanged": True,
                "before": -53, "spacer": -61 if owner == "B" else None,
                "after": 59, "valuesUnchanged": None,
            })
        for owner in ("A", "B"):
            rows.append({
                "kind": "owner-null", "owner": owner, "index": index,
                "result": None, "exception": "System.NullReferenceException",
                "sameArray": None, "slotUnchanged": None,
                "before": None, "spacer": None, "after": None,
                "valuesUnchanged": None,
            })
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "owner-indexed-enum-array" or
            report.get("platform") != platform):
        raise ValueError("Owner-indexed enum-array report has wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Owner-indexed enum-array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "owner-indexed-enum-array",
            "scope": "two owner-indexed enum getters with ordered null and bounds exits"}
