"""Independent observations for the static-field metadata guard probe."""

import json


def _int32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def observations():
    cases = (
        ("initial", 0, 4),
        ("positive", 17, -2),
        ("negative", -17, 3),
        ("positive-overflow", (1 << 31) - 1, 1),
        ("negative-overflow", -(1 << 31), -1),
        ("zero", 0, 0),
    )
    return [
        {
            "kind": kind,
            "shared": shared,
            "neighbor": 7,
            "parameter": parameter,
            "result": _int32(shared + parameter),
        }
        for kind, shared, parameter in cases
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "metadata-guard-parameter"):
        raise ValueError("Metadata guard parameter report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Metadata guard parameter native observations require a Windows player")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Metadata guard parameter behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": report["platform"], "profile": "metadata-guard-parameter",
            "scope": "first use, changed static state, unchanged neighbor and signed overflow"}
