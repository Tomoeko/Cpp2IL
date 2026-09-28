"""Independent finite oracle for a call-result Boolean field store."""

import json


def _write(kind, before, after, cell_neighbor, owner_neighbor, *,
           exception="none", owner_is_null=False, cell_is_null=False,
           derived_cell_neighbor=None, derived_owner_neighbor=None):
    return {
        "kind": kind,
        "exception": exception,
        "ownerIsNull": owner_is_null,
        "cellIsNull": None if owner_is_null else cell_is_null,
        "cellSameWitness": None if owner_is_null else not cell_is_null,
        "enabledBefore": before,
        "enabledAfter": after,
        "cellNeighborAfter": cell_neighbor,
        "touchCountAfter": None if owner_is_null else 1,
        "ownerNeighborAfter": owner_neighbor,
        "derivedCellNeighbor": derived_cell_neighbor,
        "derivedOwnerNeighbor": derived_owner_neighbor,
    }


def observations():
    return [
        {"kind": "constructors", "ownerCreated": True, "cellCreated": True,
         "cellIsNull": True, "enabled": False, "cellNeighbor": 0,
         "touchCount": 0, "ownerNeighbor": 0},
        _write("enable", False, True, 29, 13),
        _write("disable", True, False, 29, 13),
        _write("shared-enable", False, True, 29, -17),
        _write("shared-disable", True, False, 29, 13),
        _write("null-cell-enable", False, False, 29, 31,
               exception="System.NullReferenceException", cell_is_null=True),
        _write("null-cell-disable", False, False, 29, 31,
               exception="System.NullReferenceException", cell_is_null=True),
        _write("null-owner", False, False, 29, None,
               exception="System.NullReferenceException", owner_is_null=True),
        _write("derived", False, True, 41, 59,
               derived_cell_neighbor=47, derived_owner_neighbor=61),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "call-result-boolean-store" or
            report.get("platform") != platform):
        raise ValueError("Call-result Boolean store report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Call-result Boolean store behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 6,
            "platform": report["platform"], "profile": "call-result-boolean-store",
            "scope": "Effectful call precedes direct getter and Boolean store, including shared reference, unchanged neighbors, derived instances and both null paths"}
