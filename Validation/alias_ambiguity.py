"""Independent behavior oracle for the shared native body ambiguity control."""

import json


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (expected_platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "alias-ambiguity" or
            report.get("platform") != expected_platform):
        raise ValueError("Alias ambiguity report has the wrong version, stage, profile or platform")

    expected = [
        {"value": value, "first": value + 7, "second": value + 7, "caller": value + 7}
        for value in (-17, -1, 0, 1, 17)
    ]
    if report.get("observations") != expected:
        raise ValueError("Alias ambiguity behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": expected_platform, "profile": "alias-ambiguity"}
