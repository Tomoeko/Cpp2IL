"""Independent oracle for Boolean getters with distinct metadata shapes."""

import json


def observations():
    expected = []
    for value in (False, True):
        inherited_word = 0x102030405 if value else -0x102030405
        expected.append({
            "kind": "generic-owner", "valueBefore": value,
            "result": value, "valueAfter": value,
            "inheritedWordBefore": inherited_word,
            "inheritedWordAfter": inherited_word,
            "ownNeighborSame": True, "neighborAliasesPartner": value,
            "partnerValueAfter": not value, "partnerNeighborSame": True,
        })
        expected.append({
            "kind": "virtual", "dispatch": "base", "baseBefore": value,
            "overrideBefore": None, "result": value,
            "baseAfter": value, "overrideAfter": None,
        })
        for dispatch in ("base-reference-to-override", "direct-override"):
            expected.append({
                "kind": "virtual", "dispatch": dispatch, "baseBefore": not value,
                "overrideBefore": value, "result": value,
                "baseAfter": not value, "overrideAfter": value,
            })
    expected.extend({
        "kind": "null", "owner": owner,
        "exception": "System.NullReferenceException",
    } for owner in ("generic-owner", "virtual-base", "virtual-override"))
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "boolean-getter-metadata" or
            report.get("platform") != platform):
        raise ValueError("Boolean getter metadata report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Boolean getter metadata behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 11,
            "platform": report["platform"], "profile": "boolean-getter-metadata",
            "scope": "generic-ancestor layout, reference identity, virtual dispatch and null receivers"}
