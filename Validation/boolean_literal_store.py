"""Independent oracle for ordered call-result Boolean stores and null failure."""

import json


def _int32(value):
    return (value + 2147483648) % 4294967296 - 2147483648


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("platform") != platform or
            report.get("profile") != "boolean-literal-store"):
        raise ValueError("Boolean literal store report has the wrong target or stage")
    expected = []
    for operation in range(3):
        for seed in (-2147483648, -1, 0, 2147483647):
            for state in (False, True):
                for neighbor in (False, True):
                    for missing in (False, True):
                        for increment in (-2147483648, -7, 0, 2147483647):
                            marker = seed + (increment if operation == 2 else 0) + 1
                            if not missing:
                                marker += (4, 8, 2)[operation]
                            expected.append({
                                "operation": operation, "seed": seed, "state": state,
                                "neighbor": neighbor, "missing": missing, "increment": increment,
                                "marker": _int32(marker),
                                "stateAfter": state if missing else operation != 1,
                                "neighborAfter": neighbor,
                                "exception": "NullReferenceException" if missing else "none",
                                "currentMatches": True,
                            })
    if report.get("observations") != expected:
        raise ValueError("Boolean literal store observations differ from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 6,
            "platform": platform, "profile": "boolean-literal-store"}
