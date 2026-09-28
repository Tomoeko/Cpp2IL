"""Independent finite oracle for a guarded zero store through unsealed classes."""

import json


def _store(kind, before, after, neighbor, prefix, suffix, *,
           exception="none", owner_is_null=False, box_is_null=False,
           alias_count=None, derived_box_neighbor=None,
           derived_owner_neighbor=None):
    return {
        "kind": kind,
        "exception": exception,
        "ownerIsNull": owner_is_null,
        "boxIsNull": None if owner_is_null else box_is_null,
        "boxSameWitness": None if owner_is_null else not box_is_null,
        "countBefore": before,
        "countAfter": after,
        "neighborAfter": neighbor,
        "prefixAfter": prefix,
        "suffixAfter": suffix,
        "aliasCountAfter": alias_count,
        "derivedBoxNeighbor": derived_box_neighbor,
        "derivedOwnerNeighbor": derived_owner_neighbor,
    }


def observations():
    return [
        {"kind": "constructors", "ownerCreated": True, "boxCreated": True,
         "boxIsNull": True, "count": 0, "neighbor": 0,
         "prefix": 0, "suffix": 0},
        _store("basic", 71, 0, 29, -11, 13),
        _store("repeat", -(2**31), 0, 29, -11, 13),
        _store("shared", 37, 0, 29, -11, 13, alias_count=0),
        _store("null-box", 43, 43, 29, -23, 31,
               exception="System.NullReferenceException", box_is_null=True,
               alias_count=43),
        _store("null-owner", 43, 43, 29, None, None,
               exception="System.NullReferenceException", owner_is_null=True,
               alias_count=43),
        _store("derived", -53, 0, 41, -37, 59,
               derived_box_neighbor=47, derived_owner_neighbor=61),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "unsealed-zero-store" or
            report.get("platform") != platform):
        raise ValueError("Unsealed zero-store report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Unsealed zero-store behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "profile": "unsealed-zero-store",
            "scope": "Unsealed owner and field receiver, shared alias, derived instances, unchanged neighboring fields and both null paths"}
