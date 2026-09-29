"""Independent oracle for final interface slots and derived Boolean shadows."""

import json


ORDINARY_ROUTES = ("interface-value", "interface-read", "concrete-value", "concrete-read")
EXPLICIT_ROUTES = ("interface-value", "interface-read")
SHADOW_ROUTES = ("interface-value", "interface-read", "base-value", "base-read", "shadow-value", "shadow-read")


def _read(owner, route, flag, neighbor, has_reference, shadow_flag=None):
    shadow = shadow_flag is not None
    result = shadow_flag if route.startswith("shadow-") else flag
    return {
        "kind": "read", "owner": owner, "route": route,
        "flagBefore": flag, "neighborBefore": neighbor,
        "referenceNullBefore": not has_reference,
        "shadowFlagBefore": shadow_flag,
        "shadowNeighborBefore": ~neighbor if shadow else None,
        "shadowReferenceNullBefore": not has_reference if shadow else None,
        "result": result, "failure": "none",
        "flagAfter": flag, "neighborAfter": neighbor, "referenceSame": True,
        "shadowFlagAfter": shadow_flag,
        "shadowNeighborAfter": ~neighbor if shadow else None,
        "shadowReferenceSame": True if shadow else None,
    }


def observations():
    expected = [{
        "kind": "constructors",
        "ordinaryFlag": False, "ordinaryNeighbor": 0, "ordinaryReferenceNull": True,
        "explicitFlag": False, "explicitNeighbor": 0, "explicitReferenceNull": True,
        "baseFlag": False, "baseNeighbor": 0, "baseReferenceNull": True,
        "shadowFlag": False, "shadowNeighbor": 0, "shadowReferenceNull": True,
    }]
    for flag in (False, True):
        for neighbor in (-(1 << 31), 0, (1 << 31) - 1):
            for has_reference in (False, True):
                for owner, routes in (("ordinary", ORDINARY_ROUTES), ("explicit", EXPLICIT_ROUTES)):
                    expected.extend(_read(owner, route, flag, neighbor, has_reference) for route in routes)
    for flag in (False, True):
        for shadow_flag in (False, True):
            for neighbor in (-(1 << 31), 0, (1 << 31) - 1):
                for has_reference in (False, True):
                    expected.extend(_read("shadow", route, flag, neighbor, has_reference, shadow_flag)
                                    for route in SHADOW_ROUTES)
    for owner, routes in (("ordinary", ORDINARY_ROUTES), ("explicit", EXPLICIT_ROUTES), ("shadow", SHADOW_ROUTES)):
        expected.extend({"kind": "null", "owner": owner, "route": route,
                         "result": None, "failure": "System.NullReferenceException"} for route in routes)
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("platform") != platform or
            report.get("profile") != "final-interface-boolean-getter"):
        raise ValueError("Final interface getter report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Final interface getter behavior differs from the independent dispatch and state oracle")
    return {"status": "passed", "observations": len(expected), "methods": 11,
            "platform": platform, "profile": "final-interface-boolean-getter",
            "scope": "Ordinary and explicit final interface slots, derived new shadows, direct and interface null failures, unchanged Boolean/integer neighbors and reference identity; nine concrete bodies and two abstract interface declarations"}
