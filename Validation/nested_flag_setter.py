"""Independent behavior oracle for an instance store through one reference field."""

import json


def observations():
    checks = (
        ("initial", "flag-false"),
        ("initial", "other-flag-false"),
        ("null-payload", "flag-true"),
        ("null-payload", "child-identity"),
        ("null-payload", "owner-neighbor"),
        ("null-payload", "owner-marker"),
        ("null-payload", "child-neighbor"),
        ("null-payload", "child-after"),
        ("null-payload", "other-child"),
        ("non-null-payload", "flag-true"),
        ("non-null-payload", "payload-unchanged"),
        ("non-null-payload", "neighbors-unchanged"),
        ("repeat-non-null", "flag-true"),
        ("repeat-non-null", "payload-unchanged"),
        ("repeat-null", "flag-true"),
        ("repeat-null", "child-identity"),
        ("null-child", "owner-unchanged"),
        ("final", "prior-child-unchanged"),
    )
    expected = [{"subject": subject, "check": check, "result": True}
                for subject, check in checks]
    expected[16:16] = [
        {"subject": "null-child", "check": check,
         "exception": "System.NullReferenceException"}
        for check in ("null-payload", "non-null-payload")
    ]
    expected[19:19] = [
        {"subject": "null-owner", "check": check,
         "exception": "System.NullReferenceException"}
        for check in ("null-payload", "non-null-payload")
    ]
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "unused-reference-nested-store" or
            report.get("platform") != platform):
        raise ValueError("Nested flag setter report has wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Nested flag setter behavior differs from independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "unused-reference-nested-store",
            "scope": "unused reference parameter, nested Boolean store, repeated calls, neighbors and null receivers"}
