"""Independent bit-pattern oracle for native-sized field loads."""

import json


def observations():
    values = (
        ("signed-negative", -1),
        ("unsigned-high", -1),
        ("signed-zero", 0),
        ("unsigned-zero", 0),
        ("signed-positive", 0x1234),
        ("unsigned-positive", 0x5678),
        ("signed-null", None),
        ("unsigned-null", None),
    )
    result = [{"kind": kind, "value": value,
               "exception": "System.NullReferenceException" if value is None else "none"}
              for kind, value in values]
    result.append({"kind": "neighbors", "before": -37, "after": 41})
    return result


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "native-int-field" or
            stage == "player" and report.get("platform") != "WindowsPlayer"):
        raise ValueError("Native-int field report has the wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Native-int field behavior differs from the independent bit-pattern oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "profile": "native-int-field",
            "scope": "64-bit signed and unsigned native-int reads, null failures, and unchanged neighbors"}
