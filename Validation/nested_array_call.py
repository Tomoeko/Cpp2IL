"""Independent behavior oracle for the bounded nested-array call fixture."""

import json

OWNERS = ("NestedA", "NestedB", "NestedC", "DirectA", "DirectB")
EDGE_INDICES = (-1, 0, 3)


def expected_case(owner, kind, length, index):
    nested_owner = owner.startswith("Nested")
    nodes_present = kind not in ("array-null", "owner-null")
    direct = [False] * length if nodes_present else []
    nested = [False] * length if nodes_present else []
    if kind == "element-null":
        direct[0] = None
        nested[0] = None
    if kind == "link-null":
        nested[0] = None

    if kind in ("owner-null", "array-null"):
        exception = "System.NullReferenceException"
    elif index < 0 or index >= length:
        exception = "System.IndexOutOfRangeException"
    elif kind == "element-null" and index == 0:
        exception = "System.NullReferenceException"
    elif kind == "link-null" and nested_owner and index == 0:
        exception = "System.NullReferenceException"
    else:
        exception = "none"
        if nested_owner:
            nested[index] = True
        else:
            direct[index] = True
    owner_present = kind != "owner-null"
    return {"owner": owner, "kind": kind, "length": length,
            "index": index, "exception": exception,
            "directTouched": direct, "nestedTouched": nested,
            "sameArray": True if owner_present else None,
            "sameElements": True if nodes_present else None,
            "sameLinks": True if nodes_present else None,
            "before": -53 if owner_present else None,
            "spacer": -61 if owner_present and owner == "NestedC" else None,
            "after": 59 if owner_present else None}


def observations():
    for owner in OWNERS:
        for length in range(4):
            for index in range(-1, 4):
                yield expected_case(owner, "length-" + str(length), length, index)
        for index in EDGE_INDICES:
            yield expected_case(owner, "array-null", None, index)
        yield expected_case(owner, "element-null", 2, 0)
        yield expected_case(owner, "link-null", 2, 0)
        for index in EDGE_INDICES:
            yield expected_case(owner, "owner-null", None, index)


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or \
            report.get("profile") != "nested-array-call":
        raise ValueError("Nested-array call report has the wrong version, stage or profile")
    expected = list(observations())
    if report.get("observations") != expected:
        raise ValueError("Nested-array call behavior differs from the independent oracle")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Nested-array call behavior was not observed in the Windows player")
    keys = [(item["owner"], item["kind"], item["index"]) for item in expected]
    if len(keys) != len(set(keys)):
        raise ValueError("Nested-array call oracle case keys are not unique")
    return {"status": "passed", "observations": len(expected),
            "methods": 14, "platform": report["platform"],
            "scope": "five array-element calls with ordered null and bounds exits"}
