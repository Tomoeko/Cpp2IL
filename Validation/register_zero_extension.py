"""Independent value oracle for register-source unsigned widening."""

import json


WORD_VALUES = (0, 1, 0x7f, 0x80, 0xff, 0x100, 0x7fff, 0x8000, 0xff00, 0xffff)
BIASES = (-(2**31), -1, 0, 1, 2**31 - 1)


def _int32(value):
    return (value + 2**31) % 2**32 - 2**31


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (expected_platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "register-zero-extension" or
            report.get("platform") != expected_platform):
        raise ValueError("Register extension report has the wrong version, stage or platform")

    expected = []
    for width, values in ((8, range(256)), (16, WORD_VALUES)):
        for value in values:
            expected.append({"width": width, "input": value, "result": value})
            for bias in BIASES:
                expected.append({"width": width, "input": value, "bias": bias,
                                 "result": _int32(value + bias)})
    observations = report.get("observations")
    if (not isinstance(observations, list) or
            any(not isinstance(item, dict) or type(item.get("width")) is not int or
                type(item.get("input")) is not int or
                type(item.get("result")) is not int or
                ("bias" in item and type(item["bias"]) is not int) for item in observations) or
            observations != expected):
        raise ValueError("Register extension observations differ from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": expected_platform, "profile": "register-zero-extension"}
