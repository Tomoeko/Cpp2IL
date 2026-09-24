"""Independent oracle for an unsigned byte-field threshold and neighboring state."""

import json


VALUES = (0, 1, 127, 128, 255)
NEIGHBORS = (-17, 0, 17, -(1 << 31), (1 << 31) - 1)


def observations():
    expected = [
        {"kind": "value", "value": value, "neighborBefore": neighbor,
         "highBit": value >= 128, "valueAfter": value, "neighborAfter": neighbor}
        for value, neighbor in zip(VALUES, NEIGHBORS)
    ]
    expected.append({"kind": "null", "exception": "System.NullReferenceException"})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (expected_platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "byte-threshold" or
            report.get("platform") != expected_platform):
        raise ValueError("Byte-threshold report has the wrong version, stage, profile or platform")
    expected = observations()
    # JSON integers and Booleans must stay distinct; Python considers 1 equal to True.
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Byte-threshold behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "byte-threshold",
            "scope": "unsigned byte threshold, unchanged fields and null receiver"}
