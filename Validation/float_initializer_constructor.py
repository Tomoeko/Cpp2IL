"""Bitwise behavior oracle for direct Single field initializer constants."""

import json


EXPECTED_BITS = {
    "finite": 0xC1580000,
    "negativeZero": 0x80000000,
    "positiveInfinity": 0x7F800000,
    "negativeInfinity": 0xFF800000,
}


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if platform is None or report.get("unityVersion") != version or \
            report.get("stage") != stage or \
            report.get("profile") != "float-initializer-constructor" or \
            report.get("platform") != platform:
        raise ValueError("Float initializer behavior header differs")

    observations = report.get("observations")
    expected = [
        {"kind": "first", **EXPECTED_BITS},
        {"kind": "second", **EXPECTED_BITS},
        {"kind": "mutation", "separate": True,
         "firstFinite": 0, "secondFinite": EXPECTED_BITS["finite"]},
    ]
    if observations != expected:
        raise ValueError("Float initializer field bits or instance effects differ")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "checks": 11, "platform": platform,
            "scope": "finite, signed zero and both infinities by exact bits"}
