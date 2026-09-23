"""Independent exhaustive oracle for byte-field zero equality and a Boolean side effect."""

import json


def observations():
    expected = [
        {"kind": "condition", "condition": condition, "observed": observed,
         "conditionAfter": condition, "observedAfter": condition or observed}
        for condition in (False, True) for observed in (False, True)
    ]
    for kind, values in (("byte", range(256)), ("signed", range(-128, 128))):
        expected.extend({"kind": kind, "value": value, "zero": value == 0, "after": value} for value in values)
    expected.extend({"kind": "null", "member": member, "exception": "System.NullReferenceException"}
                    for member in ("condition", "byte", "signed"))
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "byte-fields":
        raise ValueError("Byte-field report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Byte-field native observations require a Windows player")
    expected = observations()
    # Keep JSON integer/Boolean types distinct: Python otherwise considers 1 equal to True.
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Byte-field behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "byte-fields",
            "scope": "exhaustive byte values, Boolean side effect and null receivers; not whole-program equivalence"}
