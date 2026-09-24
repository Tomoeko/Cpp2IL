"""Independent finite-vector oracle for call-result receiver null guards."""

import json


def observations():
    minimum = -(2**31)
    maximum = 2**31 - 1
    null_exception = "System.NullReferenceException"
    expected = [
        {"kind": "declarations", "rootExact": True,
         "nodeExact": True, "baseExact": True, "methodCountExact": True},
        {"kind": "helpers", "firstIdentity": True,
         "secondIdentity": True, "readValue": 17},
    ]
    cases = [
        ("success", 0, 17, "none", 15, -5, 17, False),
        ("repeat-success", 15, 17, "none", 30, -5, 17, False),
        ("first-null", 0, None, null_exception, 3, None, None, False),
        ("second-null", 0, None, null_exception, 7, 23, None, False),
        ("self-alias", 0, -17, "none", 15, -17, -17, True),
        ("trace-overflow", maximum, minimum, "none", minimum + 14,
         1, minimum, False),
        ("root-null", None, None, null_exception, None, None, None, False),
    ]
    for (name, initial_trace, result, exception, final_trace,
         first_value, second_value, same_reference) in cases:
        expected.append({
            "kind": "execute:" + name,
            "initialTrace": initial_trace,
            "result": result,
            "exception": exception,
            "finalTrace": final_trace,
            "firstPreserved": True,
            "nextPreserved": True,
            "firstValue": first_value,
            "secondValue": second_value,
            "sameReference": same_reference,
        })
    repeated_cases = [
        ("success", 0, 0, 0, 18, "none", 15, 2, 0x5a, 7, 11, False),
        ("first-null", 0, 0, 0, None, null_exception, 1, 1, 0,
         None, 11, False),
        ("second-null", 0, 0, 0, None, null_exception, 7, 2, 0x5a,
         7, None, False),
        ("self-alias", 0, 0, 0, -6, "none", 15, 2, 0x5a,
         -3, -3, True),
        ("sum-overflow", 0, 0, 0, minimum, "none", 15, 2, 0x5a,
         1, maximum, False),
        ("root-null", None, None, None, None, null_exception,
         None, None, None, None, None, False),
    ]
    for inherited_setter in (False, True):
        for (name, initial_trace, initial_calls, initial_marker, result,
             exception, final_trace, final_calls, final_marker, first_value,
             second_value, same_reference) in repeated_cases:
            expected.append({
                "kind": ("inherited:" if inherited_setter else "repeated:") + name,
                "initialTrace": initial_trace,
                "initialCalls": initial_calls,
                "initialMarker": initial_marker,
                "result": result,
                "exception": exception,
                "finalTrace": final_trace,
                "finalCalls": final_calls,
                "finalMarker": final_marker,
                "firstPreserved": True,
                "secondPreserved": True,
                "firstValue": first_value,
                "secondValue": second_value,
                "firstEnabled": None if first_value is None else inherited_setter,
                "secondEnabled": None if second_value is None else
                    inherited_setter and same_reference,
                "sameReference": same_reference,
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
            report.get("stage") != stage or
            report.get("profile") != "call-result-null-guards" or
            report.get("platform") != platform):
        raise ValueError("Call-result null-guard report has the wrong target or stage")
    expected = observations()
    if not same_typed_value(report.get("observations"), expected):
        raise ValueError("Call-result null-guard behavior differs from the oracle")
    return {"status": "passed", "observations": len(expected), "methods": 13,
            "platform": platform, "profile": "call-result-null-guards",
            "scope": "exact declarations, repeated call-result receivers, inherited Boolean setter, byte-store order, null exceptions, aliases and signed overflow"}
