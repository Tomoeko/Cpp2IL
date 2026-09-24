"""Independent oracle for a cast getter and cold constructor ordering."""

import json


def observations():
    def row(kind, result, exception, *, neighbor, override_before,
            override_after, current_same, current_is_derived, text, marker,
            detail, extra):
        return {
            "kind": kind, "result": result, "exception": exception,
            "neighborBefore": neighbor, "neighborAfter": neighbor,
            "overrideBefore": override_before, "overrideAfter": override_after,
            "currentSame": current_same, "currentIsDerived": current_is_derived,
            "textAfter": text, "markerAfter": marker,
            "detailAfter": detail, "extraAfter": extra,
            "outerRunsAfter": 1, "middleRunsAfter": 1,
        }

    return [
        {"kind": "cold-construction", "outerBefore": 0, "middleBefore": 0,
         "outerAfterFirst": 1, "middleAfterFirst": 1,
         "outerOrderAfterFirst": 2, "middleOrderAfterFirst": 1,
         "outerAfterSecond": 1, "middleAfterSecond": 1,
         "outerOrderAfterSecond": 2, "middleOrderAfterSecond": 1,
         "nextOrderAfterSecond": 2, "distinctInstances": True,
         "firstDetail": 0, "secondDetail": 0},
        {"kind": "initialization-order", "exception": "System.NullReferenceException",
         "outerBefore": 1, "middleBefore": 1,
         "outerAfterCast": 1, "middleAfterCast": 1,
         "outerAfterConstruction": 1, "middleAfterConstruction": 1,
         "wrongTypeException": "System.NullReferenceException",
         "outerAfterNonNullCast": 1, "middleAfterNonNullCast": 1,
         "wrongTypeTextAfter": "unchanged:", "wrongTypeMarkerAfter": 7,
         "middleTrigger": 0, "outerAfterMiddle": 1, "middleAfterMiddle": 1,
         "outerTrigger": 0, "outerAfterExplicit": 1, "middleAfterExplicit": 1,
         "neighborAfter": 5},
        row("normal-first", "pre:neutral-key", "none", neighbor=19,
            override_before=None, override_after=None, current_same=True,
            current_is_derived=True, text="pre:", marker=11, detail=13, extra=17),
        row("normal-second", "pre:neutral-key", "none", neighbor=19,
            override_before=None, override_after=None, current_same=True,
            current_is_derived=True, text="pre:", marker=11, detail=13, extra=17),
        row("null-node", None, "System.NullReferenceException", neighbor=23,
            override_before=None, override_after=None, current_same=True,
            current_is_derived=False, text=None, marker=None, detail=None, extra=None),
        row("wrong-type", None, "System.NullReferenceException", neighbor=31,
            override_before=None, override_after=None, current_same=True,
            current_is_derived=False, text="ignored:", marker=29, detail=None, extra=None),
        row("null-text", "neutral-key", "none", neighbor=43,
            override_before=None, override_after=None, current_same=True,
            current_is_derived=True, text=None, marker=37, detail=41, extra=None),
        row("null-owner", None, "System.NullReferenceException", neighbor=None,
            override_before=None, override_after=None, current_same=None,
            current_is_derived=None, text=None, marker=None, detail=None, extra=None),
        row("override-through-base", "override", "none", neighbor=59,
            override_before=0, override_after=1, current_same=True,
            current_is_derived=True, text="unused:", marker=47, detail=53, extra=None),
        row("override-direct", "override", "none", neighbor=59,
            override_before=1, override_after=2, current_same=True,
            current_is_derived=True, text="unused:", marker=47, detail=53, extra=None),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "virtual-string-call" or
            report.get("platform") != platform):
        raise ValueError("Virtual string call report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Virtual string call behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 5,
            "platform": report["platform"], "profile": "virtual-string-call",
            "scope": "cold construction, cast, class initialization, null paths and virtual dispatch"}
