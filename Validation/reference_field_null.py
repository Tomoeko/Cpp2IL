"""Independent observations for reference-field null comparisons."""

import json


def observations():
    return [
        {"kind": "initial-null", "isNull": True, "hasCurrent": False,
         "textNull": True, "trapNull": True, "trapOperatorNull": True,
         "failure": None, "neighbor": 23},
        {"kind": "assigned-values", "isNull": False, "hasCurrent": True,
         "textNull": False, "trapNull": False, "trapOperatorNull": True,
         "failure": None, "neighbor": 23},
        {"kind": "cleared-values", "isNull": True, "hasCurrent": False,
         "textNull": True, "trapNull": True, "trapOperatorNull": True,
         "failure": None, "neighbor": 23},
        {"kind": "inherited-null", "inheritedNull": True, "neighbor": -31},
        {"kind": "inherited-value", "inheritedNull": False, "neighbor": -31},
        {"kind": "null-receiver", "isNull": None, "hasCurrent": None,
         "textNull": None, "trapNull": None, "trapOperatorNull": None,
         "failure": "NullReferenceException", "neighbor": None},
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "reference-field-null"):
        raise ValueError("Reference-field-null report has wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Reference-field-null native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Reference-field-null behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 12,
            "platform": report["platform"], "profile": "reference-field-null",
            "scope": "instance and inherited reference-field null predicates and receiver effects"}
