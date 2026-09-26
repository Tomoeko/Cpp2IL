"""Independent oracle for guarded object-array element Boolean stores."""

import json


def observations():
    rows = []
    cases = [("distinct", index) for index in (-(2**31), -1, 0, 1, 2, 3, 2**31 - 1)]
    cases += [("aliased", 1), ("element-null", 1), ("element-null", -1),
              ("array-null", 1), ("array-null", -(2**31)),
              ("owner-null", 1), ("empty", 0)]
    for enable in (False, True):
        for kind, index in cases:
            states = [] if kind == "empty" else [not enable] * 3
            if kind == "element-null":
                states[1] = None
            if kind in ("owner-null", "array-null"):
                exception = "System.NullReferenceException"
            elif index < 0 or index >= len(states):
                exception = "System.IndexOutOfRangeException"
            elif states[index] is None:
                exception = "System.NullReferenceException"
            else:
                exception = "none"
                states[index] = enable
                if kind == "aliased":
                    states[2] = enable
            rows.append({"kind": kind, "index": index, "enable": enable,
                         "exception": exception, "states": states,
                         "neighborsUnchanged": True,
                         "sameArray": None if kind == "owner-null" else True,
                         "sameAlias": True if kind == "aliased" else None,
                         "ownerBefore": None if kind == "owner-null" else -29,
                         "ownerAfter": None if kind == "owner-null" else 31})
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "array-element-store" or
            report.get("platform") != platform):
        raise ValueError("Array element store report has wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Array element store behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "array-element-store",
            "scope": "Boolean literals, ordered null and unsigned bounds exits, aliases and neighbors"}
