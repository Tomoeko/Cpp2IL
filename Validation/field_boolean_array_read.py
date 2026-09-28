"""Independent oracle for guarded Boolean-array reads from instance fields."""

import json


def observations():
    rows = []

    def add(kind, slot, index, value=None, exception="none", owner=True):
        rows.append({"kind": kind, "slot": slot, "index": index,
                     "value": value, "exception": exception,
                     "before": -13 if owner else None,
                     "between": 29 if owner else None,
                     "pad6": 37 if owner else None})

    initial = ([False, True], [True, False], [False, True])
    for slot, values in enumerate(initial):
        for index in (-(2**31), -1, 0, 1, 2, 2**31 - 1):
            if 0 <= index < len(values):
                add("initial", slot, index, values[index])
            else:
                add("initial", slot, index, exception="System.IndexOutOfRangeException")

    for slot, value in enumerate((True, False, True)):
        add("mutated", slot, 0, value)
    for slot in range(3):
        add("aliased", slot, 1, False)
    for slot in range(3):
        add("alias-mutated", slot, 1, True)
    for slot in range(3):
        add("empty", slot, 0, exception="System.IndexOutOfRangeException")
    for slot in range(3):
        add("array-null", slot, 0, exception="System.NullReferenceException")
        add("owner-null", slot, 0, exception="System.NullReferenceException", owner=False)
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "field-boolean-array-read" or
            report.get("platform") != platform):
        raise ValueError("Boolean-array field read report has wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Boolean-array field read differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "field-boolean-array-read",
            "scope": "Boolean array values, unsigned bounds, null failures, aliasing and mutations"}
