"""Independent observations for an inherited, interface-bearing virtual tail."""

from behavior_oracle import verify_report


def _row(kind, exception, base_marker, derived_marker, base_tag, derived_tag):
    return {"kind": kind, "exception": exception,
            "baseMarker": base_marker, "derivedMarker": derived_marker,
            "baseNeighbor": 101, "derivedNeighbor": 202,
            "baseTag": base_tag, "derivedTag": derived_tag}


def observations():
    return [
        _row("base:first", "none", 11, 5, 7, 9),
        _row("derived:first", "none", 11, 29, 7, 9),
        _row("base:again", "none", 11, -5, 17, 19),
        _row("derived:again", "none", 11, 29, 17, 19),
        _row("null", "System.NullReferenceException", 11, 29, 17, 19),
    ]


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "layered-virtual-tail-dispatch", observations(), 8,
        "Inherited and interface-bearing virtual tail with base/override effects, "
        "neighbor and tag preservation, repeated calls, and null receiver")
