"""Independent oracle for a signed instance-field read and null receiver."""

import json


def observations():
    expected = [{"kind": str(value), "result": value, "exception": "none"}
                for value in (-(1 << 31), -17, 0, 17, (1 << 31) - 1)]
    expected.append({"kind": "null", "result": None, "exception": "System.NullReferenceException"})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "field-guard":
        raise ValueError("Field-guard report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Field-guard native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Field-guard behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": report["platform"], "profile": "field-guard",
            "scope": "signed instance-field reads and null receivers; not whole-program equivalence"}
