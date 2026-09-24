"""Independent finite oracle for folded Int32 state constructors."""

import json


def observations():
    expected = [
        {"kind": "declarations", "firstShape": True,
         "secondShape": True, "typesDistinct": True},
    ]
    for kind, state in (
            ("first-minimum", -(2**31)),
            ("second-maximum", 2**31 - 1),
            ("first-zero", 0),
            ("second-negative", -17),
            ("first-maximum", 2**31 - 1),
            ("first-reflection", 17)):
        expected.append({
            "kind": kind, "typeExact": True, "state": state,
            "neighborInitiallyNull": True, "neighborAliasSame": True,
            "receiverAliasSame": True,
        })
    expected.append({
        "kind": "independence", "firstInstancesDistinct": True,
        "secondInstancesDistinct": True, "typesDistinct": True,
        "firstMinimumRetained": True, "firstZeroRetained": True,
        "firstMaximumRetained": True, "secondMaximumRetained": True,
        "secondNegativeRetained": True, "reflectedRetained": True,
        "neighborsDistinct": True,
    })
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "folded-state-constructor" or
            report.get("platform") != platform):
        raise ValueError("Constructor report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Constructor behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "folded-state-constructor",
            "scope": "signed state, field preservation, exact declarations, aliases and fresh instances"}
