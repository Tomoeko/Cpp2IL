"""Independent oracle for a folded, literal-field constructor family."""

import json


def observations():
    result = [{"kind": "declarations", "firstShape": True,
               "secondShape": True, "thirdShape": True}]
    for kind, state in (("first", 37), ("second", 37), ("third", -19),
                        ("first-again", 37), ("second-reflection", 37)):
        result.append({"kind": kind, "typeExact": True, "state": state,
                       "neighborInitiallyNull": True, "guard": 0})
    result.append({
        "kind": "independence", "firstAndRepeatedDistinct": True,
        "secondAndReflectedDistinct": True, "typesDistinct": True,
        "firstNeighborAlias": True, "firstGuard": 71, "firstState": 37,
        "repeatedNeighborNull": True, "repeatedGuard": 0, "repeatedState": 37,
        "secondState": 37, "thirdState": -19, "reflectedState": 37,
    })
    return result


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "folded-literal-constructor" or
            report.get("platform") != platform or
            report.get("observations") != observations()):
        raise ValueError("Folded literal-constructor behavior differs from the oracle")
    return {"status": "passed", "observations": 7, "methods": 3,
            "platform": platform, "profile": "folded-literal-constructor",
            "scope": "literal field effects, constructor identity, aliases and fresh instances"}
