"""Independent behavior oracle for a nullable target Boolean zero store."""

import json


def observations():
    return [
        {
            "kind": "true", "flagBefore": True, "flagAfter": False,
            "exception": "none", "sameTargetReference": True,
            "targetNeighborBefore": 53, "targetNeighborAfter": 53,
            "ownerNeighborBefore": -71, "ownerNeighborAfter": -71,
            "paddingBefore": 120, "paddingAfter": 120,
        },
        {
            "kind": "false", "flagBefore": False, "flagAfter": False,
            "exception": "none", "sameTargetReference": True,
            "targetNeighborBefore": -53, "targetNeighborAfter": -53,
            "ownerNeighborBefore": 71, "ownerNeighborAfter": 71,
            "paddingBefore": 120, "paddingAfter": 120,
        },
        {
            "kind": "target-null", "flagBefore": None, "flagAfter": None,
            "exception": "System.NullReferenceException", "sameTargetReference": True,
            "targetNeighborBefore": None, "targetNeighborAfter": None,
            "ownerNeighborBefore": -71, "ownerNeighborAfter": -71,
            "paddingBefore": 120, "paddingAfter": 120,
        },
        {
            "kind": "owner-null", "flagBefore": None, "flagAfter": None,
            "exception": "System.NullReferenceException", "sameTargetReference": None,
            "targetNeighborBefore": None, "targetNeighborAfter": None,
            "ownerNeighborBefore": None, "ownerNeighborAfter": None,
            "paddingBefore": None, "paddingAfter": None,
        },
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "nested-boolean-store" or
            report.get("platform") != platform):
        raise ValueError("Boolean store report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Nullable Boolean store behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "profile": "nested-boolean-store",
            "scope": "Boolean mutation, alias identity, unchanged neighbors, and distinct null failures"}
