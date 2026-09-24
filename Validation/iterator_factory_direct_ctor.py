"""Independent behavior oracle for the direct-constructor iterator factory."""

import json


def observations():
    checks = (
        ("factory", "fresh"),
        ("first", "state-zero"),
        ("second", "state-zero"),
        ("first", "owner-captured"),
        ("second", "owner-captured"),
        ("first", "neighbor-untouched"),
        ("second", "neighbor-untouched"),
        ("first", "interface-current"),
        ("first", "interface-completed"),
        ("owner", "marker-unchanged"),
        ("first", "reset-keeps-state"),
        ("first", "dispose-keeps-owner"),
        ("other", "owner-captured"),
        ("other", "marker-unchanged"),
        ("other", "neighbor-untouched"),
        ("constructor", "signed-minimum"),
        ("constructor", "signed-maximum"),
        ("constructor", "fresh-boundary-instances"),
        ("constructor", "owner-unassigned"),
        ("constructor", "neighbor-unassigned"),
    )
    expected = [
        {"subject": subject, "check": check, "result": True}
        for subject, check in checks
    ]
    expected.append({
        "subject": "null-owner", "check": "factory-call",
        "exception": "System.NullReferenceException",
    })
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "iterator-factory-direct-ctor" or
            report.get("platform") != platform):
        raise ValueError("Direct-constructor iterator report has wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Direct-constructor iterator behavior differs from independent oracle")
    return {"status": "passed", "observations": len(expected),
            "platform": report["platform"], "profile": "iterator-factory-direct-ctor",
            "scope": "fresh instances, signed state, owner and neighbor fields, interface dispatch and null owner"}
