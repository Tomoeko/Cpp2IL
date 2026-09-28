"""Independent observations for three ordered null guards and literal selection."""

import json


def _case(kind, first_null=False, second_null=False, third_null=False,
          flag=True, label="left-", owner_null=False):
    exception = "System.NullReferenceException" if (
        first_null or second_null or third_null or owner_null) else "none"
    result = None if exception != "none" else (label or "") + (
        "warm" if flag else "cool") + "!ending"
    return {"kind": kind, "result": result, "exception": exception,
            "ownerIsNull": owner_null,
            "firstIsNull": None if owner_null else first_null,
            "secondIsNull": None if owner_null else second_null,
            "thirdIsNull": third_null,
            "firstSameWitness": None if owner_null else not first_null,
            "secondSameWitness": None if owner_null else not second_null,
            "getterCount": None if owner_null else
            (1 if first_null else 2),
            "nestedGetterCount": 0 if (owner_null or first_null or second_null) else 1,
            "flagAfter": flag, "labelAfter": label}


def observations():
    return [
        _case("false", flag=False),
        _case("true"),
        _case("null-label", label=None),
        _case("first-null", first_null=True),
        _case("second-null", second_null=True),
        _case("third-null", third_null=True),
        _case("owner-null", owner_null=True),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "triple-literal-guard" or
            report.get("platform") != platform):
        raise ValueError("Triple-literal guard report has the wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Triple-literal guard behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 6,
            "platform": platform, "profile": "triple-literal-guard",
            "scope": "three ordered null guards and two literal choices"}
