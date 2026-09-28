"""Independent behavior oracle for inherited fixed-index reference reads."""

import json


ARRAYS = ("owner-null", "array-null", "empty", "single", "two",
          "covariant-two", "alias-two")
GETTERS = (("first", 0), ("second", 1))
INITIAL = {
    "owner-null": None,
    "array-null": None,
    "empty": [],
    "single": ["first"],
    "two": ["first", "second"],
    "covariant-two": ["derived-first", "derived-second"],
    "alias-two": ["first", "first"],
}


def observations():
    rows = []
    for getter, index in GETTERS:
        for kind in ARRAYS:
            values = INITIAL[kind]
            if kind in ("owner-null", "array-null"):
                exception = "System.NullReferenceException"
            elif index >= len(values):
                exception = "System.IndexOutOfRangeException"
            else:
                exception = "none"
            rows.append({
                "getter": getter, "array": kind, "index": index,
                "exception": exception,
                "result": values[index] if exception == "none" else None,
                "sameReference": True if exception == "none" else None,
                "before": values, "after": values,
                "sameArray": None if kind == "owner-null" else True,
                "ownerBefore": None if kind == "owner-null" else -31,
                "ownerAfter": None if kind == "owner-null" else 37,
            })
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "inherited-reference-array-read" or
            report.get("platform") != platform or
            report.get("observations") != observations()):
        raise ValueError("Inherited reference array read behavior differs from the oracle")
    return {"status": "passed", "observations": len(observations()),
            "methods": 6, "platform": platform,
            "profile": "inherited-reference-array-read",
            "scope": "Inherited array field identity, null and bounds failures, covariance, and neighbors"}
