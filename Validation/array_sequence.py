"""Independent finite oracle for two ordered accesses through an Int32 array field."""

import json


MINIMUM = -(1 << 31)
MAXIMUM = (1 << 31) - 1
SCENARIOS = (
    ("null-owner", True, None, False),
    ("null-array", False, None, False),
    ("empty-shared", False, (), True),
    ("single-shared", False, (17,), True),
    ("mixed-shared", False, (MINIMUM, -7, MAXIMUM), True),
    ("mixed-distinct", False, (MINIMUM, -7, MAXIMUM), False),
)


def index_cases(length):
    return (
        ("same-index", 0, 0),
        ("last-to-first", length - 1, 0),
        ("first-to-last", 0, length - 1),
        ("negative-first", -1, 0),
        ("negative-second", 0, -1),
        ("upper-first", length, 0),
        ("upper-second", 0, length),
        ("both-negative", -1, -1),
        ("both-upper", length, length),
        ("minimum-first", MINIMUM, MAXIMUM),
        ("maximum-second", 0, MAXIMUM),
    )


def int32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def expected_case(label, missing_owner, initial, share_alias, initial_counter,
                  kind, first, second):
    length = len(initial) if initial is not None else 0
    before = list(initial) if initial is not None and not missing_owner else None
    after = None if before is None else before.copy()
    alias_before = None if before is None else before.copy()
    alias_after = None if alias_before is None else alias_before.copy()
    counter = None if missing_owner else initial_counter
    last_read = None if missing_owner else -999
    result = None

    if missing_owner or initial is None:
        exception = "System.NullReferenceException"
    elif first < 0 or first >= length:
        exception = "System.IndexOutOfRangeException"
    else:
        read_value = initial[first]
        last_read = read_value
        counter = int32(counter + 1)
        if second < 0 or second >= length:
            exception = "System.IndexOutOfRangeException"
        else:
            exception = "none"
            result = read_value
            after[second] = read_value
            if share_alias:
                alias_after[second] = read_value

    return {
        "kind": kind, "case": label,
        "firstIndex": first, "secondIndex": second,
        "result": result, "exception": exception,
        "before": before, "after": after,
        "aliasBefore": alias_before, "aliasAfter": alias_after,
        "counterBefore": None if missing_owner else initial_counter,
        "counterAfter": counter,
        "lastReadBefore": None if missing_owner else -999,
        "lastReadAfter": last_read,
        "aliasCounterAfter": None if missing_owner else 777,
        "aliasLastReadAfter": None if missing_owner else -777,
        "sameFieldReference": not missing_owner,
        "aliasSharesArray": not missing_owner and initial is not None and share_alias,
    }


def observations():
    expected = []
    for label, missing_owner, initial, share_alias in SCENARIOS:
        length = len(initial) if initial is not None else 0
        for kind, first, second in index_cases(length):
            expected.append(expected_case(label, missing_owner, initial, share_alias,
                                          23, kind, first, second))
    expected.append(expected_case("counter-overflow-second-fails", False,
                                  (17,), True, MAXIMUM,
                                  "upper-second-after-overflow", 0, 1))
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
    if platform is None or report.get("unityVersion") != version or \
            report.get("stage") != stage or report.get("profile") != "array-sequence" or \
            report.get("platform") != platform:
        raise ValueError("Array-sequence report has the wrong version, stage, profile or platform")
    expected = observations()
    if not same_typed_value(report.get("observations"), expected):
        raise ValueError("Array-sequence behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "array-sequence",
            "scope": "ordered Int32 array field read and write, side effects, Int32 counter wrap, null and bounds failures, aliasing"}
