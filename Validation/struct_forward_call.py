"""Independent bitwise oracle for a by-value float pair forwarded through a field."""

import json


def _case(kind, first, second, previous_first, previous_second,
          neighbor, prefix, suffix, result, *, exception="none",
          owner_is_null=False, receiver_is_null=False, ignored_is_null=False,
          alias_prefix=None, alias_suffix=None):
    has_owner = not owner_is_null
    has_alias = alias_prefix is not None
    copied_first = previous_first if exception != "none" else first
    copied_second = previous_second if exception != "none" else second
    return {
        "kind": kind, "first": first, "second": second,
        "result": result, "exception": exception,
        "sourceFirstAfter": first, "sourceSecondAfter": second,
        "lastFirstBefore": previous_first,
        "lastSecondBefore": previous_second,
        "lastFirstAfter": copied_first,
        "lastSecondAfter": copied_second,
        "lastFirstAfterSourceChange": copied_first,
        "lastSecondAfterSourceChange": copied_second,
        "neighborAfter": neighbor,
        "ownerIsNull": owner_is_null,
        "receiverIsNull": receiver_is_null if has_owner else None,
        "receiverSameBefore": True if has_owner else None,
        "receiverSameWitness": (not receiver_is_null) if has_owner else None,
        "prefixAfter": prefix, "suffixAfter": suffix,
        "ignoredIsNull": ignored_is_null,
        "markerBefore": None if ignored_is_null else 17,
        "markerAfter": None if ignored_is_null else 17,
        "aliasSameWitness": True if has_alias else None,
        "aliasLastFirstAfter": copied_first if has_alias else None,
        "aliasLastSecondAfter": copied_second if has_alias else None,
        "aliasPrefixAfter": alias_prefix,
        "aliasSuffixAfter": alias_suffix,
    }


def observations():
    zero = "00000000"
    negative_zero = "80000000"
    one = "3f800000"
    negative_one = "bf800000"
    infinity = "7f800000"
    maximum_finite = "7f7fffff"
    nan = "7fc00001"
    subnormal = "00000001"
    three = "40400000"
    negative_three = "c0400000"
    four = "40800000"
    negative_four = "c0800000"
    return [
        {"kind": "constructors", "ownerCreated": True, "targetCreated": True,
         "tokenCreated": True, "freshReceiverIsNull": True,
         "freshPrefix": 0, "freshSuffix": 0,
         "freshNeighbor": 0, "freshLastFirst": zero,
         "freshLastSecond": zero, "freshMarker": 0},
        _case("negative-zero", negative_zero, zero, zero, zero,
              37, -11, 13, True),
        _case("positive-zero", zero, negative_zero, negative_zero, zero,
              37, -11, 13, True, ignored_is_null=True),
        _case("positive", one, negative_one, zero, negative_zero,
              37, -11, 13, True),
        _case("negative", negative_one, one, one, negative_one,
              37, -11, 13, True),
        _case("infinity", infinity, maximum_finite, negative_one, one,
              37, -11, 13, True),
        _case("nan", nan, one, infinity, maximum_finite,
              37, -11, 13, True),
        _case("subnormal", subnormal, one, nan, one,
              37, -11, 13, True),
        _case("overwrite", one, zero, subnormal, one,
              37, -11, 13, True),
        _case("shared-left", one, negative_one, zero, zero,
              43, -17, 19, True,
              alias_prefix=-23, alias_suffix=29),
        _case("shared-right", negative_one, one, one, negative_one,
              43, -23, 29, True,
              alias_prefix=-17, alias_suffix=19),
        _case("null-receiver", one, zero, three, negative_three,
              43, -31, 31, None,
              exception="System.NullReferenceException", receiver_is_null=True),
        _case("null-owner", one, zero, four, negative_four,
              53, None, None, None,
              exception="System.NullReferenceException", owner_is_null=True,
              alias_prefix=-37, alias_suffix=37),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "struct-forward-call" or
            report.get("platform") != platform):
        raise ValueError("Struct-forward-call report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Struct-forward-call behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 5,
            "platform": report["platform"], "profile": "struct-forward-call",
            "scope": "by-value float pair, stored copy, ignored class argument, aliasing, unchanged neighbors, and both null paths"}
