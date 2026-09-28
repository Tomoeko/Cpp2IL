"""Finite oracle for a 32-bit enum returned through a guarded field receiver."""

import json


def _case(kind, value, neighbor, first, last, *, exception="none",
          owner_is_null=False, receiver_is_null=False, receiver_same_witness=True,
          alias_value=None, alias_first=None, alias_last=None):
    has_owner = not owner_is_null
    has_alias = alias_first is not None
    return {
        "kind": kind, "exception": exception, "ownerIsNull": owner_is_null,
        "receiverIsNull": receiver_is_null if has_owner else None,
        "result": value if exception == "none" else None,
        "valueBefore": value, "valueAfter": value, "neighborAfter": neighbor,
        "receiverSameBefore": True if has_owner else None,
        "receiverSameWitness": receiver_same_witness if has_owner else None,
        "firstPadAfter": first, "lastPadAfter": last,
        "aliasSameWitness": True if has_alias else None,
        "aliasValueAfter": alias_value,
        "aliasFirstPadAfter": alias_first,
        "aliasLastPadAfter": alias_last,
    }


def observations():
    return [
        {"kind": "fresh", "receiverIsNull": True, "readerValue": 0,
         "readerNeighbor": 0, "firstPad": 0, "lastPad": 0},
        _case("negative", -1, 37, -11, 13),
        _case("zero", 0, 37, -11, 13),
        _case("one", 1, 37, -11, 13),
        _case("maximum", (1 << 31) - 1, 37, -11, 13),
        _case("minimum", -(1 << 31), 37, -11, 13),
        _case("alias-left", -1, 43, -17, 19,
              alias_value=-1, alias_first=-23, alias_last=29),
        _case("alias-right", (1 << 31) - 1, 43, -23, 29,
              alias_value=(1 << 31) - 1, alias_first=-17, alias_last=19),
        _case("null-receiver", 1, 47, -31, 31,
              exception="System.NullReferenceException", receiver_is_null=True,
              receiver_same_witness=False),
        _case("null-owner", -1, 53, None, None,
              exception="System.NullReferenceException", owner_is_null=True,
              alias_value=-1, alias_first=-37, alias_last=37),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "enum-return-tail" or
            report.get("platform") != platform):
        raise ValueError("Enum-return tail report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Enum-return tail behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "enum-return-tail",
            "scope": "Int32 enum edge values, aliasing, unchanged fields, and both null paths"}
