"""Independent finite oracle for a cross-assembly static sink call."""

import json


def _row(kind, exception, init_before, init_after, calls_before, calls_after,
         literal, identity):
    return {
        "kind": kind,
        "exception": exception,
        "initializationsBefore": init_before,
        "initializationsAfter": init_after,
        "callsBefore": calls_before,
        "callsAfter": calls_after,
        "eventsBefore": init_before + calls_before,
        "eventsAfter": init_after + calls_after,
        "initializationOrder": 1 if init_after else 0,
        "lastCallOrder": init_after + calls_after if calls_after else 0,
        "initializationsAtLastCall": 1 if calls_after else 0,
        "lastLiteral": literal,
        "lastExceptionIdentity": identity,
        "leftAliasSame": True,
        "ownersDistinct": True,
        "leftNeighborSame": True,
        "rightNeighborSame": True,
    }


def observations():
    null_reference = "System.NullReferenceException"
    return [
        _row("initial", "none", 0, 0, 0, 0, None, "null"),
        _row("null-cold", null_reference, 0, 0, 0, 0, None, "null"),
        _row("left-north-cold", "none", 0, 1, 0, 1, "north", "first"),
        _row("right-south", "none", 1, 1, 1, 2, "south", "second"),
        _row("left-north-null", "none", 1, 1, 2, 3, "north", "null"),
        _row("right-north-first", "none", 1, 1, 3, 4, "north", "first"),
        _row("left-south-null", "none", 1, 1, 4, 5, "south", "null"),
        _row("null-warm", null_reference, 1, 1, 5, 5, "south", "null"),
        {
            "kind": "declarations",
            "northExact": True,
            "southExact": True,
            "wrappersDistinct": True,
            "ownerHasNoStaticConstructor": True,
            "sinkHasExplicitStaticConstructor": True,
            "sinkExact": True,
            "sinkExternal": True,
        },
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "guarded-sink" or
            report.get("platform") != platform):
        raise ValueError("Guarded sink report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Guarded sink behavior differs from the independent oracle")
    return {
        "status": "passed",
        "observations": len(expected),
        "methods": 2,
        "platform": report["platform"],
        "profile": "guarded-sink",
        "scope": "cold sink initialization, call order, literal and exception identity, "
                 "receiver aliases, per-call counts and null-owner calls",
    }
