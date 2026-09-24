"""Independent finite-vector oracle for sequential reference null guards."""

import json


def int32(value):
    return (value + 2**31) % 2**32 - 2**31


def observations():
    minimum = -(2**31)
    maximum = 2**31 - 1
    comparisons = [
        ("both-null", None, None, True),
        ("left-null", None, (7, 97), False),
        ("right-null", (-7, 91), None, False),
        ("equal-zero", (0, 31), (0, 47), False),
        ("less", (-17, 32), (17, 48), False),
        ("greater", (17, 33), (-17, 49), False),
        ("minimum-maximum", (minimum, 34), (maximum, 50), False),
        ("maximum-minimum", (maximum, 35), (minimum, 51), False),
        ("minimum-equal", (minimum, 36), (minimum, 52), False),
        ("maximum-equal", (maximum, 37), (maximum, 53), False),
        ("equal-distinct", (17, 38), (17, 54), False),
        ("same-object", (17, 71), (17, 71), True),
    ]
    expected = []
    for kind, left, right, same_reference in comparisons:
        missing = left is None or right is None
        result = None if missing else (-1 if left[0] < right[0] else
                                       1 if left[0] > right[0] else 0)
        expected.append({
            "kind": "compare:" + kind,
            "result": result,
            "exception": "System.NullReferenceException" if missing else "none",
            "leftValue": None if left is None else left[0],
            "rightValue": None if right is None else right[0],
            "leftNeighbor": None if left is None else left[1],
            "rightNeighbor": None if right is None else right[1],
            "sameReference": same_reference,
        })

    single_guard = [
        ("null-zero", None, 0),
        ("null-maximum", None, maximum),
        ("zero", (0, 81), 0),
        ("mixed", (23, 82), -17),
        ("minimum", (minimum, 83), -7),
        ("maximum", (maximum, 84), 1),
        ("wrapped", (-17, 85), maximum),
    ]
    for kind, box, amount in single_guard:
        expected.append({
            "kind": "read-after-add:" + kind,
            "amount": amount,
            "result": None if box is None else int32(int32(amount + 7) + box[0]),
            "exception": "System.NullReferenceException" if box is None else "none",
            "value": None if box is None else box[0],
            "neighbor": None if box is None else box[1],
        })
    return expected


def same_typed_value(actual, expected):
    if type(actual) is not type(expected):
        return False
    if isinstance(expected, dict):
        return actual.keys() == expected.keys() and all(
            same_typed_value(actual[key], value) for key, value in expected.items())
    if isinstance(expected, list):
        return len(actual) == len(expected) and all(
            same_typed_value(left, right) for left, right in zip(actual, expected))
    return actual == expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "sequential-null-guards" or
            report.get("platform") != platform):
        raise ValueError("Sequential null-guard report has the wrong version, stage, profile or platform")
    expected = observations()
    if not same_typed_value(report.get("observations"), expected):
        raise ValueError("Sequential null-guard behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": platform, "profile": "sequential-null-guards",
            "scope": "finite two-reference comparisons, one-reference arithmetic, null exceptions and unchanged fields"}
