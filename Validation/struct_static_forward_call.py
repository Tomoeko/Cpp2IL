"""Independent oracle for forwarding a struct with separate static initialization."""

import json


def _invoke(kind, first, second, before_first, before_second, neighbor,
            prefix, suffix, result, events, *, exception="none",
            owner_is_null=False, receiver_is_null=False, ignored_is_null=False):
    stored_first = before_first if exception != "none" else first
    stored_second = before_second if exception != "none" else second
    return {
        "kind": kind, "first": first, "second": second,
        "result": result, "exception": exception,
        "eventsBefore": events, "eventsAfter": events,
        "sourceFirstAfter": first, "sourceSecondAfter": second,
        "lastFirstBefore": before_first, "lastSecondBefore": before_second,
        "lastFirstAfter": stored_first, "lastSecondAfter": stored_second,
        "lastFirstAfterSourceChange": stored_first,
        "lastSecondAfterSourceChange": stored_second,
        "neighborAfter": neighbor,
        "ownerIsNull": owner_is_null,
        "receiverIsNull": None if owner_is_null else receiver_is_null,
        "receiverSameBefore": None if owner_is_null else True,
        "receiverSameWitness": None if owner_is_null else not receiver_is_null,
        "prefixAfter": prefix, "suffixAfter": suffix,
        "ignoredIsNull": ignored_is_null,
        "markerBefore": None if ignored_is_null else 17,
        "markerAfter": None if ignored_is_null else 17,
    }


def observations():
    zero = "00000000"
    negative_zero = "80000000"
    subnormal = "00000001"
    infinity = "7f800000"
    negative_infinity = "ff800000"
    nan = "7fc00001"
    maximum_finite = "7f7fffff"
    return [
        {"kind": "default-value", "eventsBefore": 0, "eventsAfter": 0,
         "first": negative_zero, "second": subnormal},
        _invoke("null-owner", negative_zero, subnormal, "3f800000", "bf800000",
                41, None, None, None, 0,
                exception="System.NullReferenceException", owner_is_null=True),
        _invoke("null-receiver", negative_zero, subnormal,
                infinity, negative_infinity, 43, -7, 11, None, 0,
                exception="System.NullReferenceException", receiver_is_null=True),
        _invoke("forward-before-static-read", negative_zero, subnormal,
                zero, zero, 47, -13, 19, True, 0),
        {"kind": "explicit-static-read", "eventsBefore": 0,
         "eventsAfter": 1, "marker": 37, "bias": negative_zero},
        _invoke("forward-after-static-read", nan, maximum_finite,
                negative_zero, subnormal, 47, -13, 19, True, 1,
                ignored_is_null=True),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "struct-static-forward-call" or
            report.get("platform") != platform):
        raise ValueError("Static-struct report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Static-struct behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 6,
            "platform": report["platform"], "profile": "struct-static-forward-call",
            "scope": "default value and forwarding before cctor, explicit static read, later forwarding, value copy and both null paths"}
