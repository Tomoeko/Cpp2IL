"""Bit-level oracle for exact positive scalar zero returns."""

import json


EXPECTED = [
    {"kind": "single", "bits": "00000000"},
    {"kind": "double", "bits": "0000000000000000"},
]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "scalar-zero-return" or
            report.get("platform") != platform):
        raise ValueError("Scalar zero report has the wrong version, stage or platform")
    if report.get("observations") != EXPECTED:
        raise ValueError("Scalar zero return bits differ from the independent oracle")
    return {"status": "passed", "observations": len(EXPECTED), "methods": 2,
            "platform": platform, "profile": "scalar-zero-return"}
