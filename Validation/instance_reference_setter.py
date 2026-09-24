"""Independent behavior oracle for direct instance reference-property setters."""

import json


def observations():
    checks = (
        ("reference", "starts-null"),
        ("reference", "distinct-cells"),
        ("reference", "first-value-identity"),
        ("reference", "neighbor-untouched"),
        ("reference", "marker-untouched"),
        ("reference", "other-cell-untouched"),
        ("reference", "alias-replacement"),
        ("reference", "old-value-replaced"),
        ("reference", "separate-cell-value"),
        ("reference", "separate-cell-neighbor"),
        ("reference", "self-reference"),
        ("reference", "null-clears-value"),
        ("reference", "neighbor-after-clear"),
    )
    expected = [
        {"subject": subject, "check": check, "result": True}
        for subject, check in checks
    ]
    expected.extend((
        {"subject": "reference", "check": "null-receiver-value",
         "exception": "System.NullReferenceException"},
        {"subject": "reference", "check": "null-receiver-null",
         "exception": "System.NullReferenceException"},
    ))
    expected.extend({"subject": "text", "check": check, "result": True} for check in (
        "starts-null", "value-identity", "neighbor-untouched",
        "replacement-identity", "marker-untouched", "null-clears-value",
        "neighbor-after-clear",
    ))
    expected.append({"subject": "text", "check": "null-receiver",
                     "exception": "System.NullReferenceException"})
    expected.extend({"subject": "declaration", "check": check, "result": True}
                    for check in ("reference-property-type", "text-property-type"))
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "instance-reference-setter" or
            report.get("platform") != platform):
        raise ValueError("Instance reference setter report has wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Instance reference setter behavior differs from independent oracle")
    return {"status": "passed", "observations": len(expected),
            "platform": report["platform"], "profile": "instance-reference-setter",
            "scope": "direct reference field identity, replacement, null clearing, receiver aliasing and null receivers"}
