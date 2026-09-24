"""Finite signed-Int32 oracle for the dense-switch decode-boundary fixture."""

import json


INT_MIN = -(1 << 31)
INT_MAX = (1 << 31) - 1
DENSE_SELECTORS = (INT_MIN, -2, -1, *range(16), 16, 17, INT_MAX)
SPARSE_SELECTORS = (INT_MIN, -100001, -100000, -17, -1, 0, 1, 29,
                    30, 99999, 100000, 100001, INT_MAX)
VALUES = (INT_MIN, -17, 0, 19, INT_MAX)
INITIAL_TRACES = (INT_MIN, 0, INT_MAX)


def int32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def dense(selector, value, trace):
    if selector == 0:
        trace = int32(trace * 3 + 101)
        result = value + trace
    elif selector == 1:
        trace = int32(trace * 5 - 37)
        result = value - trace
    elif selector == 2:
        trace = int32(trace ^ 0x4a391827)
        result = value ^ trace
    elif selector == 3:
        trace = int32(trace + value + 211)
        result = value | trace
    elif selector == 4:
        trace = int32(trace - value - 313)
        result = value & trace
    elif selector == 5:
        trace = int32(trace * 7 + 419)
        result = (value << 3) + trace
    elif selector == 6:
        trace = int32(trace * 11 - 521)
        result = (value >> 2) ^ trace
    elif selector == 7:
        trace = int32(trace ^ int32(value << 1))
        result = value * (trace | 1)
    elif selector == 8:
        trace = int32(trace + (value >> 3))
        result = -value + trace
    elif selector == 9:
        trace = int32(trace - int32(value << 2))
        result = int32(value << 5) | (trace >> 27)
    elif selector == 10:
        trace = int32(trace * 13 + 631)
        result = value - (trace >> 4)
    elif selector == 11:
        trace = int32((trace >> 1) ^ value)
        result = (value & trace) + 701
    elif selector == 12:
        trace = int32(trace + 809)
        result = (value ^ (value >> 16)) + trace
    elif selector == 13:
        trace = int32(trace - 907)
        result = (value * 17) ^ trace
    elif selector == 14:
        trace = int32(trace * 17 + 1009)
        result = (value | 0x55aa55aa) - trace
    elif selector == 15:
        trace = int32(trace ^ 1103)
        result = (value & 0x7f7f7f7f) + int32(trace << 1)
    else:
        trace = int32(trace + 1201)
        result = value ^ trace
    return int32(result), trace


def sparse(selector, value, trace):
    if selector == -100000:
        trace = int32(trace + 17)
        result = value + trace
    elif selector == -17:
        trace = int32(trace ^ 0x13579bdf)
        result = value ^ trace
    elif selector == 0:
        trace = int32(trace - 23)
        result = value - trace
    elif selector == 29:
        trace = int32(trace * 7)
        result = value * trace
    elif selector == 100000:
        trace = int32(trace + value)
        result = value | trace
    elif selector == INT_MAX:
        trace = int32(trace - value)
        result = value & trace
    else:
        trace = int32(trace ^ 41)
        result = value + trace
    return int32(result), trace


def observations():
    rows = []
    for method, selectors, operation in (("dense", DENSE_SELECTORS, dense),
                                          ("sparse", SPARSE_SELECTORS, sparse)):
        for selector in selectors:
            for value in VALUES:
                for initial in INITIAL_TRACES:
                    result, trace = operation(selector, value, initial)
                    rows.append({"method": method, "selector": selector,
                                 "value": value, "initialTrace": initial,
                                 "trace": trace, "result": result,
                                 "exception": "none"})
    return rows


def _same_typed_value(actual, expected):
    if type(actual) is not type(expected):
        return False
    if isinstance(expected, dict):
        return actual.keys() == expected.keys() and all(
            _same_typed_value(actual[key], value) for key, value in expected.items())
    if isinstance(expected, list):
        return len(actual) == len(expected) and all(
            _same_typed_value(left, right) for left, right in zip(actual, expected))
    return actual == expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("platform") != platform or
            report.get("profile") != "dense-switch"):
        raise ValueError("Dense-switch report has the wrong target or stage")

    expected = observations()
    if not _same_typed_value(report.get("observations"), expected):
        raise ValueError("Dense-switch behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected),
            "methods": 2, "platform": platform, "profile": "dense-switch",
            "scope": "all 16 dense cases, sparse control, signed boundaries, 32-bit wrap and trace mutation"}
