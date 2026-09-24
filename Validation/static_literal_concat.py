"""Independent finite behavior oracle for the static literal-concat control."""

import json


def observations():
    cases = [
        ("null", None, "|tag", 43),
        ("empty", "", "|tag", -17),
        ("first", "vv", "vv|tag", 0),
        ("repeat", "vv", "vv|tag", 101),
        ("unicode", "\u03a9\u0301\U0001f642", "\u03a9\u0301\U0001f642|tag", -59),
        ("suffix-inside", "x|tag", "x|tag|tag", 2**31 - 1),
    ]
    return [
        {"kind": label, "inputAfter": value, "sameInputAfter": True,
         "result": result, "sameResultAsInput": False, "exception": "none",
         "neighborBefore": neighbor, "neighborAfter": neighbor}
        for label, value, result, neighbor in cases
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "static-literal-concat" or
            report.get("platform") != platform):
        raise ValueError("Static-literal report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Static-literal behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": platform, "profile": "static-literal-concat",
            "scope": "null, empty, repeated and Unicode literal concatenation; identity and unchanged neighbor"}
