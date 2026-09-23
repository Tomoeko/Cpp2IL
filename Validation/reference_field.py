"""Independent oracle for direct and nested string-field reads."""

import json


def observations():
    expected = [{"kind": "constructors", "boxCreated": True, "outerCreated": True}]
    for label, value in (("value", "qqq"), ("empty", ""), ("null-value", None)):
        for prefix in ("direct", "nested"):
            expected.append({"kind": prefix + "-" + label, "result": value,
                             "sameReference": True, "exception": "none"})
    for kind in ("direct-null-owner", "nested-null-inner", "nested-null-outer"):
        expected.append({"kind": kind, "result": None, "sameReference": False,
                         "exception": "System.NullReferenceException"})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "reference-field":
        raise ValueError("Reference-field report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Reference-field native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Reference-field behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "reference-field",
            "scope": "direct and one-level nested string-field reads with null owners; not whole-program equivalence"}
