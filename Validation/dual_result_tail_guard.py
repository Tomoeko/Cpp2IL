"""Independent observations for sequential guarded call results."""

import json


def _call(kind, first_null=False, second_null=False, owner_null=False):
    if owner_null:
        return {"kind": kind, "exception": "System.NullReferenceException",
                "ownerIsNull": True, "firstIsNull": None,
                "secondIsNull": None, "firstSameWitness": None,
                "secondSameWitness": None, "firstGetterCount": None,
                "secondGetterCount": None, "stage": None,
                "firstApplyCount": 0,
                "lastValueAfter": True, "secondFinishCount": 0}
    return {"kind": kind,
            "exception": "System.NullReferenceException" if first_null or second_null else "none",
            "ownerIsNull": False, "firstIsNull": first_null,
            "secondIsNull": second_null, "firstSameWitness": not first_null,
            "secondSameWitness": not second_null, "firstGetterCount": 1,
            "secondGetterCount": 0 if first_null else 1,
            "stage": 1 if first_null else 3,
            "firstApplyCount": 0 if first_null else 1,
            "lastValueAfter": first_null,
            "secondFinishCount": 0 if first_null or second_null else 1}


def observations():
    return [
        {"kind": "constructors", "ownerCreated": True, "firstCreated": True,
         "secondCreated": True, "firstIsNull": True, "secondIsNull": True,
         "stage": 0},
        _call("base-success"),
        _call("base-first-null", first_null=True),
        _call("base-second-null", second_null=True),
        _call("alias-one-success"),
        _call("alias-one-first-null", first_null=True),
        _call("alias-two-success"),
        _call("alias-two-second-null", second_null=True),
        _call("null-owner", owner_null=True),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "dual-result-tail-guard" or
            report.get("platform") != platform):
        raise ValueError("Dual-result tail report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Dual-result tail behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 12,
            "platform": report["platform"], "profile": "dual-result-tail-guard",
            "scope": "two ordered guarded call results and separate null failures"}
