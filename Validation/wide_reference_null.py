"""Independent observations for reference null tests at wider instance offsets."""

from behavior_oracle import verify_report


def _row(kind, near, first, second, alias=False):
    return {
        "kind": kind,
        "hasNear": near, "hasNearRepeat": near,
        "hasFirst": first, "hasFirstRepeat": first,
        "isSecondNull": not second, "isSecondNullRepeat": not second,
        "nearFailure": "none", "firstFailure": "none", "secondFailure": "none",
        "nearNullBefore": not near, "firstNullBefore": not first,
        "secondNullBefore": not second,
        "nearSame": True, "firstSame": True, "secondSame": True,
        "pad00Same": True, "pad06Same": True, "gapSame": True,
        "allTargetsAlias": alias,
    }


def observations():
    rows = [
        _row("all-null", False, False, False),
        _row("near-only", True, False, False),
        _row("first-only", False, True, False),
        _row("second-only", False, False, True),
        _row("all-values", True, True, True),
        _row("shared-target", True, True, True, alias=True),
        _row("cleared", False, False, False),
    ]
    rows.append({
        "kind": "null-owner",
        "hasNear": None, "hasNearRepeat": None,
        "hasFirst": None, "hasFirstRepeat": None,
        "isSecondNull": None, "isSecondNullRepeat": None,
        "nearFailure": "System.NullReferenceException",
        "firstFailure": "System.NullReferenceException",
        "secondFailure": "System.NullReferenceException",
        "nearNullBefore": None, "firstNullBefore": None,
        "secondNullBefore": None,
        "nearSame": None, "firstSame": None, "secondSame": None,
        "pad00Same": None, "pad06Same": None, "gapSame": None,
        "allTargetsAlias": None,
    })
    return rows


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "wide-reference-null", observations(), 4,
        "instance reference-null predicates at near and wider field offsets; receiver exceptions and unchanged fields")
