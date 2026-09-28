"""Independent wraparound oracle for a Boolean low-byte arithmetic branch."""

import json


VALUES = (-(2**31), -(2**31) + 16, -18, -1, 0, 1, 18,
          2**31 - 1 - 16, 2**31 - 1)


def _int32(value):
    return (value + 2**31) % 2**32 - 2**31


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (expected_platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "narrow-test-arithmetic" or
            report.get("platform") != expected_platform):
        raise ValueError("Narrow arithmetic report has the wrong version, stage or platform")

    expected = [{"value": value, "add": add,
                 "selected": _int32(value + 17 if add else value - 17)}
                for value in VALUES for add in (False, True)]
    observations = report.get("observations")
    if (not isinstance(observations, list) or
            any(not isinstance(item, dict) or type(item.get("value")) is not int or
                type(item.get("add")) is not bool or
                type(item.get("selected")) is not int for item in observations) or
            observations != expected):
        raise ValueError("Narrow arithmetic observations differ from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": expected_platform, "profile": "narrow-test-arithmetic"}
