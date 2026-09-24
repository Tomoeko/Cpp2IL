"""Independent finite behavior oracle for terminal managed throws."""

import json


def _observation(kind, result, exception, first_current, first_calls,
                 second_current, second_calls):
    return {
        "kind": kind,
        "result": result,
        "exception": exception,
        "firstCurrent": first_current,
        "firstNeighbor": 23,
        "firstCalls": first_calls,
        "secondCurrent": second_current,
        "secondNeighbor": -29,
        "secondCalls": second_calls,
    }


def observations():
    initial = (5, 0, 11, 0)
    first_return = (10, 1, 11, 0)
    first_overflow = (-(2**31) + 16, 2, 11, 0)
    second_underflow = (-(2**31) + 16, 2, -(2**31) + 18, 1)
    return [
        _observation("unsupported:first", None, "System.NotSupportedException", *initial),
        _observation("unimplemented:first", None, "System.NotImplementedException", *initial),
        _observation("unsupported:second", None, "System.NotSupportedException", *initial),
        _observation("unsupported:null", None, "System.NullReferenceException", *initial),
        _observation("unimplemented:null", None, "System.NullReferenceException", *initial),
        _observation("return:first-negative", 10, "none", *first_return),
        _observation("return:first-overflow", -(2**31) + 16, "none", *first_overflow),
        _observation("return:second-underflow", -(2**31) + 18, "none", *second_underflow),
        _observation("return:null", None, "System.NullReferenceException", *second_underflow),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "throw-only" or
            report.get("platform") != platform):
        raise ValueError("Throw-only report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Throw-only behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 5,
            "platform": platform, "profile": "throw-only",
            "scope": "two exact exception types, null owners, returning calls, signed wraparound and unchanged fields"}
