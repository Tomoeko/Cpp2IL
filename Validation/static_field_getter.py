"""Independent behavior oracle for the static-field getter fixture."""

import json


def observations():
    return [
        {"kind": kind, "pointer": value, "referenceMatches": True,
         "storedPointer": value, "storedReferenceMatches": True,
         "expectedPointer": value}
        for kind, value in (("initial", 0), ("first", 17), ("second", -17),
                            ("minimum", -(1 << 63)))
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "static-field-getter"):
        raise ValueError("Static getter report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Static getter native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Static getter behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "profile": "static-field-getter",
            "scope": "native pointer values, object identity, static state and repeated reads"}
