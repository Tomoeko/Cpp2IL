"""Independent behavior oracle for the exact-target iterator factory fixture."""

import json


def observations():
    checks = (
        ("factory", "fresh"),
        ("factory", "initial-current-null"),
        ("first", "first-move"),
        ("first", "deferred-owner-field"),
        ("second", "first-move"),
        ("second", "deferred-owner-field"),
        ("first", "completed"),
        ("second", "completed"),
        ("owner", "field-unchanged"),
        ("null-marker", "first-move"),
        ("null-marker", "current-null"),
        ("null-marker", "completed"),
        ("other-owner", "first-move"),
        ("other-owner", "captured-own-field"),
    )
    expected = [
        {"subject": subject, "check": check, "result": True}
        for subject, check in checks[:9]
    ]
    expected.extend((
        {"subject": "first", "check": "dispose-exception", "exception": "none"},
        {"subject": "first", "check": "reset-exception",
         "exception": "System.NotSupportedException"},
    ))
    expected.extend(
        {"subject": subject, "check": check, "result": True}
        for subject, check in checks[9:]
    )
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "iterator-factory" or
            report.get("platform") != platform):
        raise ValueError("Iterator report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Iterator behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected),
            "platform": report["platform"], "profile": "iterator-factory",
            "scope": "fresh instances, deferred owner read, completion, disposal and reset"}
