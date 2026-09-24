"""Independent behavior oracle for the direct-state iterator factory fixture."""

import json


def observations():
    checks = (
        ("factory", "fresh"),
        ("first", "state-zero"),
        ("second", "state-zero"),
        ("first", "owner-captured"),
        ("second", "owner-captured"),
        ("first", "current-owner"),
        ("first", "completed"),
        ("owner", "marker-unchanged"),
        ("first", "reset-keeps-state"),
        ("first", "dispose-keeps-owner"),
        ("other", "owner-captured"),
        ("other", "marker-unchanged"),
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
            report.get("profile") != "iterator-factory-variant" or
            report.get("platform") != platform):
        raise ValueError("Iterator variant report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Iterator variant behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected),
            "platform": report["platform"], "profile": "iterator-factory-variant",
            "scope": "fresh instances, state, owner capture, interface methods and null owner"}
