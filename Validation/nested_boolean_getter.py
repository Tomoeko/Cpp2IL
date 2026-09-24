"""Independent behavior oracle for a Boolean field read through one reference field."""

import json


def observations():
    checks = (
        ("first-false", "result"),
        ("first-false", "identity"),
        ("first-false", "neighbors"),
        ("first-true", "result"),
        ("first-true", "repeat"),
        ("first-true", "neighbors"),
        ("second-false", "result"),
        ("second-false", "identity"),
        ("second-false", "neighbors"),
        ("second-true", "result"),
        ("second-true", "first-unchanged"),
        ("null-child", "owner-unchanged"),
        ("final", "children-unchanged"),
    )
    expected = [{"subject": subject, "check": check, "result": True}
                for subject, check in checks]
    expected[11:11] = [{"subject": "null-child",
                        "exception": "System.NullReferenceException"}]
    expected[13:13] = [{"subject": "null-owner",
                        "exception": "System.NullReferenceException"}]
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "nested-boolean-getter" or
            report.get("platform") != platform):
        raise ValueError("Nested Boolean getter report has wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Nested Boolean getter behavior differs from independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "profile": "nested-boolean-getter",
            "scope": "nested Boolean reads, repeated reads, neighbors and null receivers"}
