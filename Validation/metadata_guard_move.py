"""Independent observations for the metadata guard register-move probe."""

import json


def _int32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def observations():
    cases = (
        ("initial", 0, 0, 4),
        ("positive", 17, -5, -2),
        ("negative", -17, 23, 0),
        ("positive-overflow", (1 << 31) - 1, 1, 1),
        ("negative-overflow", -(1 << 31), -1, -1),
        ("null-box", 99, None, 0),
    )
    return [
        {
            "kind": kind,
            "shared": shared,
            "neighbor": 7,
            "parameter": parameter,
            "boxValue": box,
            "parameterResult": _int32(shared + parameter),
            "boxResult": None if box is None else _int32(shared + box),
            "boxError": "System.NullReferenceException" if box is None else None,
        }
        for kind, shared, box, parameter in cases
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "metadata-guard-move"):
        raise ValueError("Metadata guard move report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Metadata guard move native observations require a Windows player")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Metadata guard move behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "metadata-guard-move",
            "scope": "first use, changed static state, null receiver and signed overflow"}
