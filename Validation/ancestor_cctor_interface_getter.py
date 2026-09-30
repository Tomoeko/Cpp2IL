"""Neutral oracle for a final interface field getter below a class initializer."""

from behavior_oracle import verify_report


def observations():
    rows = []
    for flag in (False, True):
        for marker in (-(1 << 31), 0, (1 << 31) - 1):
            rows.append({
                "kind": "read", "flag": flag, "marker": marker,
                "initialFlag": False, "initialMarker": 0,
                "first": flag, "second": flag,
                "flagAfter": flag, "markerAfter": marker,
                "receiverSame": True,
            })
    rows.append({"kind": "null-interface", "failure": "System.NullReferenceException"})
    return rows


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "ancestor-cctor-interface-getter", observations(), 3,
        "Final interface Boolean field read below a framework class initializer; "
        "repeated reads, untouched integer neighbor, and null interface dispatch")
