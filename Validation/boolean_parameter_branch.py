"""Independent behavior oracle for the second-argument Boolean branch control."""

import json


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (expected_platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "boolean-parameter-branch" or
            report.get("platform") != expected_platform):
        raise ValueError("Boolean branch report has the wrong version, stage, profile or platform")

    observations = report.get("observations")
    if not isinstance(observations, list):
        raise ValueError("Boolean branch observations must be a list")
    for item in observations:
        if (not isinstance(item, dict) or type(item.get("value")) is not int or
                type(item.get("useHighPath")) is not bool or
                any(type(item.get(key)) is not int for key in ("selected", "high", "low"))):
            raise ValueError("Boolean branch observations have invalid operand types")

    expected = []
    for value in (-(2**31), -17, -1, 0, 1, 17, 2**31 - 1):
        for use_high_path in (False, True):
            high = 17
            low = -17
            expected.append({"value": value, "useHighPath": use_high_path,
                             "selected": high if use_high_path else low,
                             "high": high, "low": low})
    if observations != expected:
        raise ValueError("Boolean branch behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": expected_platform, "profile": "boolean-parameter-branch"}
