"""Independent behavior oracle for array access through an instance field."""

import json


MINIMUM = -(1 << 31)
MAXIMUM = (1 << 31) - 1
SCENARIOS = (
    ("null-owner", True, None, None, None),
    ("null-array", False, None, 17, -17),
    ("empty", False, (), -31, 31),
    ("single", False, (MINIMUM,), 101, -101),
    ("mixed", False, (-7, 0, MAXIMUM), -303, 303),
)


def observations():
    expected = []
    for label, missing_owner, initial, neighbor, alias_neighbor in SCENARIOS:
        length = len(initial) if initial is not None else 0
        indices = (MINIMUM, -1, 0, 1, length - 1, length, MAXIMUM)
        operations = [("read-first", None, None)]
        operations.extend(("read-at", index, None) for index in indices)
        operations.extend(("write-at", index, value)
                          for index in indices for value in (MINIMUM, MAXIMUM))

        for kind, index, value in operations:
            before = list(initial) if initial is not None else None
            after = list(initial) if initial is not None else None
            access_index = 0 if kind == "read-first" else index
            result = None
            if missing_owner or initial is None:
                exception = "System.NullReferenceException"
            elif access_index < 0 or access_index >= length:
                exception = "System.IndexOutOfRangeException"
            else:
                exception = "none"
                if kind == "write-at":
                    after[access_index] = value
                else:
                    result = initial[access_index]

            expected.append({
                "kind": kind, "case": label, "index": index, "value": value,
                "result": result, "exception": exception,
                "before": before, "after": after,
                "aliasAfter": after if not missing_owner else None,
                "sameFieldReference": not missing_owner,
                "aliasSharesArray": not missing_owner and initial is not None,
                "neighborBefore": neighbor, "neighborAfter": neighbor,
                "aliasNeighborBefore": alias_neighbor,
                "aliasNeighborAfter": alias_neighbor,
            })
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "field-array" or
            report.get("platform") != platform):
        raise ValueError("Field-array report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Field-array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "field-array",
            "scope": "instance field array reads/writes, null and bounds failures, aliasing and unchanged state"}
