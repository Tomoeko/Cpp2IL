"""Independent oracle for array-returning calls and a call-result array read."""

import json


MINIMUM = -(1 << 31)
MAXIMUM = (1 << 31) - 1
NULL_EXCEPTION = "System.NullReferenceException"
BOUNDS_EXCEPTION = "System.IndexOutOfRangeException"


def observations():
    expected = [{"kind": "constructor", "created": True, "callsAfter": 0}]
    for calls, (kind, values) in enumerate((("values", [-(1 << 31), 0, (1 << 31) - 1]),
                                            ("empty", []), ("null-array", None)), start=1):
        expected.append({"kind": kind, "result": values, "sameReference": True,
                         "exception": "none", "callsAfter": calls})
    expected.append({"kind": "overflow", "result": [-(1 << 31), (1 << 31) - 1],
                     "sameReference": True, "exception": "none", "callsAfter": -(1 << 31)})
    for kind in ("null-receiver-values", "null-receiver-null-array"):
        expected.append({"kind": kind, "result": None, "sameReference": False,
                         "exception": "System.NullReferenceException", "callsAfter": None})
    expected.append({"kind": "derived-values", "result": [-(1 << 31), 0, (1 << 31) - 1],
                     "sameReference": True, "exception": "none", "callsAfter": 1})
    expected.append({"kind": "derived-null-receiver", "result": None, "sameReference": False,
                     "exception": "System.NullReferenceException", "callsAfter": None})
    for kind, result, same_reference, exception, first_calls, second_calls in (
        ("two-receivers", [-(1 << 31), 0, (1 << 31) - 1], True, "none", 1, 1),
        ("first-null", None, False, "System.NullReferenceException", None, 0),
        ("second-null", None, False, "System.NullReferenceException", 1, None),
    ):
        expected.append({"kind": kind, "result": result, "sameReference": same_reference,
                         "exception": exception, "firstCalls": first_calls, "secondCalls": second_calls})
    for kind, result, same_reference, exception, calls_after in (
        ("field-values", [-(1 << 31), 0, (1 << 31) - 1], True, "none", 1),
        ("field-null-values", None, True, "none", 1),
        ("field-null-receiver", None, False, "System.NullReferenceException", None),
    ):
        expected.append({"kind": kind, "result": result, "sameReference": same_reference,
                         "exception": exception, "callsAfter": calls_after})
    expected.append({"kind": "field-null-owner", "exception": "System.NullReferenceException"})
    for kind, index, argument, returned, result, exception, calls_before, calls_after in (
        ("indexed-distinct-first", 0, [7, 8, 9], [MINIMUM, 0, MAXIMUM],
         MINIMUM, "none", 0, 1),
        ("indexed-distinct-last", 2, [17, 19, 23], [MINIMUM, 0, MAXIMUM],
         MAXIMUM, "none", 0, 1),
        ("indexed-null-return", 0, [11, 13], None, None, NULL_EXCEPTION, 0, 1),
        ("indexed-null-argument", 1, None, [-17, 19], 19, "none", 0, 1),
        ("indexed-empty-return", 0, [11], [], None, BOUNDS_EXCEPTION, 0, 1),
        ("indexed-empty-argument", 0, [], [42], 42, "none", 0, 1),
        ("indexed-negative", -1, [1, 2], [3, 5], None, BOUNDS_EXCEPTION, 0, 1),
        ("indexed-upper", 2, [1, 2, 3], [4, 5], None, BOUNDS_EXCEPTION, 0, 1),
        ("indexed-minimum", MINIMUM, [-17, 19], [3, 5],
         None, BOUNDS_EXCEPTION, 0, 1),
        ("indexed-maximum", MAXIMUM, [-17, 19], [3, 5],
         None, BOUNDS_EXCEPTION, 0, 1),
        ("indexed-overflow-value", 1, [0, 1], [MINIMUM, MAXIMUM],
         MAXIMUM, "none", MAXIMUM, MINIMUM),
        ("indexed-overflow-bounds", 2, [1, 2, 3], [MINIMUM, MAXIMUM],
         None, BOUNDS_EXCEPTION, MAXIMUM, MINIMUM),
        ("indexed-null-receiver", 0, [17, 19], None,
         None, NULL_EXCEPTION, None, None),
        ("indexed-null-receiver-null-argument", 0, None, None,
         None, NULL_EXCEPTION, None, None),
    ):
        expected.append({"kind": kind, "index": index, "result": result,
                         "exception": exception, "callsBefore": calls_before,
                         "callsAfter": calls_after, "argumentAfter": argument,
                         "returnedAfter": returned})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "array-call":
        raise ValueError("Array-call report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Array-call native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Array-call behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 10,
            "platform": report["platform"], "profile": "array-call",
            "scope": "array identity, base and derived calls, guarded field arguments, call-result indexing, ordered effects, null and bounds failures; not whole-program equivalence"}
