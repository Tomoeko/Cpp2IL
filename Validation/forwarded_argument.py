"""Independent finite oracle for a one-argument call through a reference field."""

import json


def _call(kind, value, ignored, before, after, neighbor, prefix, suffix, result,
          *, exception="none", owner_is_null=False, receiver_is_null=False,
          alias_count=None, alias_prefix=None, alias_suffix=None):
    has_owner = not owner_is_null
    has_alias = alias_prefix is not None
    return {
        "kind": kind, "value": value, "ignored": ignored,
        "result": result, "exception": exception, "ownerIsNull": owner_is_null,
        "receiverIsNull": receiver_is_null if has_owner else None,
        "receiverSameBefore": True if has_owner else None,
        "receiverSameWitness": (not receiver_is_null) if has_owner else None,
        "countBefore": before, "countAfter": after, "neighborAfter": neighbor,
        "prefixAfter": prefix, "suffixAfter": suffix,
        "aliasSameWitness": True if has_alias else None,
        "aliasCountAfter": alias_count,
        "aliasPrefixAfter": alias_prefix, "aliasSuffixAfter": alias_suffix,
    }


def observations():
    maximum = (1 << 31) - 1
    minimum = -(1 << 31)
    return [
        {"kind": "constructors", "ownerCreated": True, "targetCreated": True,
         "freshReceiverIsNull": True, "freshPrefix": 0, "freshSuffix": 0,
         "freshCalls": 0, "freshNeighbor": 0},
        _call("positive", 7, minimum, 0, 1, 37, -11, 13, True),
        _call("zero", 0, maximum, 1, 2, 37, -11, 13, False),
        _call("negative", -1, 7, 2, 3, 37, -11, 13, False),
        _call("maximum", maximum, minimum, 3, 4, 37, -11, 13, True),
        _call("minimum", minimum, maximum, 4, 5, 37, -11, 13, False),
        _call("overflow", 1, 0, maximum, minimum, 37, -11, 13, True),
        _call("shared-left", 1, -999, -2, -1, 43, -17, 19, True,
              alias_count=-1, alias_prefix=-23, alias_suffix=29),
        _call("shared-right", 0, 999, -1, 0, 43, -23, 29, False,
              alias_count=0, alias_prefix=-17, alias_suffix=19),
        _call("shared-left-again", -1, minimum, 0, 1, 43, -17, 19, False,
              alias_count=1, alias_prefix=-23, alias_suffix=29),
        _call("null-receiver", 1, 2, 41, 41, 43, -31, 31, None,
              exception="System.NullReferenceException", receiver_is_null=True),
        _call("null-owner", 1, 2, 47, 47, 53, None, None, None,
              exception="System.NullReferenceException", owner_is_null=True,
              alias_count=47, alias_prefix=-37, alias_suffix=37),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "forwarded-argument" or
            report.get("platform") != platform):
        raise ValueError("Forwarded-argument report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Forwarded-argument behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "forwarded-argument",
            "scope": "one forwarded argument, ignored second argument, target side effect, signed overflow, aliasing, unchanged neighbors, and both null paths"}
