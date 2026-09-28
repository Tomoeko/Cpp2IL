"""Independent oracle for reference-array element scalar-field reads."""

import json

METHODS = ("level", "amount", "fraction")
INDICES = (-2, -1, 0, 1, 2, 3, 2**31 - 1)
EDGE_INDICES = (-1, 0, 3)
LEVELS = (-(2**31), -19, 2**31 - 1)
AMOUNTS = (2**31 - 1, 47, -(2**31))
FRACTIONS = ("-1.25", "0.5", "15.75")


def expected_case(method, kind, length, index):
    nodes_present = kind not in ("array-null", "owner-null")
    levels = list(LEVELS[:length]) if nodes_present else []
    amounts = list(AMOUNTS[:length]) if nodes_present else []
    fractions = list(FRACTIONS[:length]) if nodes_present else []
    if kind == "element-null":
        levels[0] = None
        amounts[0] = None
        fractions[0] = None
    if kind in ("owner-null", "array-null"):
        exception = "System.NullReferenceException"
        value = None
    elif index < 0 or index >= length:
        exception = "System.IndexOutOfRangeException"
        value = None
    elif levels[index] is None:
        exception = "System.NullReferenceException"
        value = None
    else:
        exception = "none"
        value = {"level": levels, "amount": amounts,
                 "fraction": fractions}[method][index]
    owner_present = kind != "owner-null"
    return {"method": method, "kind": kind, "length": length,
            "index": index, "exception": exception, "result": value,
            "levels": levels, "amounts": amounts,
            "fractions": fractions,
            "sameArray": True if owner_present else None,
            "sameElements": True if nodes_present else None,
            "unchangedLevels": True, "unchangedAmounts": True,
            "unchangedFractions": True,
            "before": -53 if owner_present else None,
            "spacer": 61 if owner_present else None,
            "after": -67 if owner_present else None}


def observations():
    for method in METHODS:
        for length in range(4):
            for index in INDICES:
                yield expected_case(method, "length-" + str(length), length, index)
        for index in EDGE_INDICES:
            yield expected_case(method, "array-null", None, index)
        yield expected_case(method, "element-null", 2, 0)
        yield expected_case(method, "element-null", 2, 1)
        for index in EDGE_INDICES:
            yield expected_case(method, "owner-null", None, index)


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or \
            report.get("profile") != "array-element-scalar-field":
        raise ValueError("Array-element scalar-field report metadata differs")
    expected = list(observations())
    if report.get("observations") != expected:
        raise ValueError("Array-element scalar-field behavior differs")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Array-element scalar-field behavior did not run in Windows player")
    keys = [(item["method"], item["kind"], item["index"]) for item in expected]
    if len(keys) != len(set(keys)):
        raise ValueError("Array-element scalar-field oracle keys are not unique")
    return {"status": "passed", "observations": len(expected),
            "methods": 5, "platform": report["platform"],
            "scope": "three reference-array element scalar reads with ordered null and bounds exits"}
