"""Finite behavior oracle for forwarding through an inline-eligible accessor."""

import json


def observations():
    def row(kind, result, exception, prepare, receiver, clock, prepare_order,
            receiver_order, at_receiver, number, current, argument,
            replace=False, throwing=False):
        return {
            "kind": kind,
            "result": result,
            "exception": exception,
            "prepareCalls": prepare,
            "receiverCalls": receiver,
            "clock": clock,
            "prepareOrder": prepare_order,
            "receiverOrder": receiver_order,
            "prepareCallsAtReceiver": at_receiver,
            "lastNumber": number,
            "current": current,
            "lastArgument": argument,
            "neighborPreserved": True,
            "replacePending": replace,
            "throwPending": throwing,
        }

    minimum = -(2**31)
    maximum = 2**31 - 1
    return [
        {"kind": "declarations", "forwardersExact": True,
         "receiversExact": True, "prepareExact": True, "accessorExact": True,
         "stateFieldsExact": True, "separateTypes": True,
         "noStaticConstructors": True},
        row("initial", None, "none", 0, 0, 0, 0, 0, 0, 0, "null", "null"),
        row("cold-null", False, "none", 1, 1, 2, 1, 2, 1, 0,
            "null", "null"),
        row("first-reference", True, "none", 2, 2, 4, 3, 4, 2, 0,
            "first", "first"),
        row("repeat-reference", True, "none", 3, 3, 6, 5, 6, 3, 0,
            "first", "first"),
        row("prepare-mutation", True, "none", 4, 4, 8, 7, 8, 4, minimum,
            "second", "second"),
        row("signed-maximum", False, "none", 5, 5, 10, 9, 10, 5, maximum,
            "second", "second"),
        row("signed-negative", True, "none", 6, 6, 12, 11, 12, 6, -17,
            "second", "second"),
        row("null-two-argument", False, "none", 7, 7, 14, 13, 14, 7, -1,
            "null", "null"),
        row("prepare-throw", None, "System.InvalidOperationException",
            8, 7, 15, 15, 14, 7, -1, "second", "null",
            replace=True, throwing=True),
        row("after-throw", True, "none", 9, 8, 17, 16, 17, 9, -1,
            "first", "first"),
    ]


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
            report.get("stage") != stage or
            report.get("profile") != "metadata-accessor" or
            report.get("platform") != platform):
        raise ValueError("Metadata-accessor report has the wrong target or stage")
    expected = observations()
    if not same_typed_value(report.get("observations"), expected):
        raise ValueError("Metadata-accessor behavior differs from the oracle")
    return {"status": "passed", "observations": len(expected), "methods": 6,
            "platform": platform, "profile": "metadata-accessor",
            "scope": "accessor declaration, preparation call order, static field forwarding, cold and repeated calls, mutation, null, signed values and preparation exception"}
