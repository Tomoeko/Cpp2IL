"""Independent behavior oracle for a direct managed reference-field store."""

import json


def _entry(kind, *, next_is_null, same_previous, same_owner,
           next_marker, previous_marker, value_marker):
    return {
        "kind": kind, "exception": "none", "nextIsNull": next_is_null,
        "nextSameValue": True, "nextSamePrevious": same_previous,
        "nextSameOwner": same_owner, "nextMarker": next_marker,
        "previousMarker": previous_marker, "valueMarker": value_marker,
        "prefixSame": True, "suffixSame": True,
        "prefixMarker": -11, "suffixMarker": 13, "ownerMarker": 29,
    }


def observations():
    expected = [
        _entry("new-value", next_is_null=False, same_previous=False,
               same_owner=False, next_marker=41, previous_marker=None,
               value_marker=41),
        _entry("replace-value", next_is_null=False, same_previous=False,
               same_owner=False, next_marker=47, previous_marker=43,
               value_marker=47),
        _entry("clear-value", next_is_null=True, same_previous=False,
               same_owner=False, next_marker=None, previous_marker=53,
               value_marker=None),
        _entry("already-null", next_is_null=True, same_previous=True,
               same_owner=False, next_marker=None, previous_marker=None,
               value_marker=None),
        _entry("same-value", next_is_null=False, same_previous=True,
               same_owner=False, next_marker=59, previous_marker=59,
               value_marker=59),
        _entry("owner-as-value", next_is_null=False, same_previous=False,
               same_owner=True, next_marker=29, previous_marker=61,
               value_marker=29),
    ]
    for kind, value_marker in (("null-owner-value", 67), ("null-owner-null", None)):
        expected.append({
            "kind": kind, "exception": "System.NullReferenceException",
            "nextIsNull": None, "nextSameValue": None,
            "nextSamePrevious": None, "nextSameOwner": None,
            "nextMarker": None, "previousMarker": None,
            "valueMarker": value_marker, "prefixSame": None,
            "suffixSame": None, "prefixMarker": None,
            "suffixMarker": None, "ownerMarker": None,
        })
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "reference-store"):
        raise ValueError("Reference-store report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Reference-store native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Reference-store behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "reference-store",
            "scope": "direct class reference field store, including null owner and value, aliasing and unchanged neighbors"}
