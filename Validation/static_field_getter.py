"""Independent behavior oracle for the static-field getter fixture."""

import json


def observations():
    return [
        {"kind": kind, "pointer": value, "referenceMatches": True,
         "storedPointer": value, "storedReferenceMatches": True,
         "expectedPointer": value, "flag": flag, "storedFlag": flag,
         "neighborFlag": neighbor, "expectedFlag": flag,
         "expectedNeighborFlag": neighbor}
        for kind, value, flag, neighbor in (("initial", 0, False, False),
                                            ("first", 17, True, False),
                                            ("second", -17, False, True),
                                            ("minimum", -(1 << 63), True, False))
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
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "static-field-getter",
            "scope": "native pointer values, object identity, Boolean values, neighboring static field and repeated reads"}
