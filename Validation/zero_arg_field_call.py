"""Independent finite oracle for a zero-argument call through one reference field."""

import json


def _call(kind, before, after, neighbor, prefix, suffix, *,
          exception="none", owner_is_null=False, receiver_is_null=False,
          alias_count=None, alias_prefix=None, alias_suffix=None):
    has_owner = not owner_is_null
    has_alias = alias_prefix is not None
    return {
        "kind": kind, "exception": exception, "ownerIsNull": owner_is_null,
        "receiverIsNull": receiver_is_null if has_owner else None,
        "receiverSameBefore": True if has_owner else None,
        "receiverSameWitness": (not receiver_is_null) if has_owner else None,
        "countBefore": before, "countAfter": after, "neighborAfter": neighbor,
        "prefixAfter": prefix, "suffixAfter": suffix,
        "aliasSameWitness": True if has_alias else None,
        "aliasCountAfter": alias_count,
        "aliasPrefixAfter": alias_prefix, "aliasSuffixAfter": alias_suffix,
    }


def _read(kind, value, neighbor, prefix, suffix, *, exception="none",
          receiver_same=True):
    return {
        "kind": kind, "exception": exception,
        "result": value if exception == "none" else None,
        "callsBefore": value, "callsAfter": value,
        "neighborAfter": neighbor,
        "prefixAfter": prefix, "suffixAfter": suffix,
        "receiverSameWitness": receiver_same,
    }


def observations():
    return [
        {"kind": "constructors", "ownerCreated": True, "receiverCreated": True,
         "freshReceiverIsNull": True, "freshPrefix": 0, "freshSuffix": 0,
         "freshCalls": 0, "freshNeighbor": 0},
        _call("first", 0, 1, 37, -11, 13),
        _call("second", 1, 2, 37, -11, 13),
        _call("third", 2, 3, 37, -11, 13),
        _call("overflow", (1 << 31) - 1, -(1 << 31), 37, -11, 13),
        _call("shared-left", -2, -1, 43, -17, 19,
              alias_count=-1, alias_prefix=-23, alias_suffix=29),
        _call("shared-right", -1, 0, 43, -23, 29,
              alias_count=0, alias_prefix=-17, alias_suffix=19),
        _call("shared-left-again", 0, 1, 43, -17, 19,
              alias_count=1, alias_prefix=-23, alias_suffix=29),
        _call("null-receiver", 41, 41, 43, -31, 31,
              exception="System.NullReferenceException", receiver_is_null=True),
        _call("null-owner", 47, 47, 53, None, None,
              exception="System.NullReferenceException", owner_is_null=True,
              alias_count=47, alias_prefix=-37, alias_suffix=37),
        {"kind": "folded-target-control", "result": -(1 << 31),
         "callsAfter": -(1 << 31), "neighborAfter": 59},
        _read("read-first", -(1 << 31), 37, -11, 13),
        _read("read-minimum", -(1 << 31), 37, -11, 13),
        _read("read-maximum", (1 << 31) - 1, 37, -11, 13),
        _read("read-null-receiver", 41, 43, -31, 31,
              exception="System.NullReferenceException", receiver_same=False),
        _read("read-null-owner", 47, 53, None, None,
              exception="System.NullReferenceException", receiver_same=None),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "zero-arg-field-call" or
            report.get("platform") != platform):
        raise ValueError("Zero-argument field-call report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Zero-argument field-call behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 8,
            "platform": report["platform"], "profile": "zero-arg-field-call",
            "scope": "zero-argument instance calls through one reference field, folded getter target, repeated and aliased mutation, unchanged neighbors, Int32 returns, and both null paths"}
