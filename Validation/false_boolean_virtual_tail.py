"""Independent typed oracle for a literal-false virtual tail call."""

from behavior_oracle import verify_report


def _row(kind, exception, base_marker, override_marker, calls, last, events,
         base_neighbor=101, override_neighbor=202):
    return {
        "kind": kind, "exception": exception,
        "baseMarker": base_marker, "baseNeighbor": base_neighbor,
        "overrideMarker": override_marker, "overrideNeighbor": override_neighbor,
        "overrideCalls": calls, "overrideLast": last, "events": events,
    }


def observations():
    return [
        _row("constructors:defaults", "none", 0, 0, 0, False, "", 0, 0),
        _row("base:first", "none", 11, 7, 0, False, "[]"),
        _row("override:first", "none", 11, 29, 1, False, "[][F]"),
        _row("override:direct-true", "none", 11, 23, 2, True, "[][F][T]"),
        _row("override:again", "none", 11, 29, 3, False, "[][F][T][F]"),
        _row("base:again", "none", 11, 29, 3, False, "[][F][T][F][]"),
        _row("override:after-reset", "none", 11, 29, 4, False,
             "[][F][T][F][][F]"),
        _row("null-owner", "System.NullReferenceException", 11, 29, 4,
             False, "[][F][T][F][][F][]"),
    ]


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "false-boolean-virtual-tail", observations(), 3,
        "Three authored methods: one public class constructor, one virtual Boolean sink, "
        "and one literal-false forwarding tail; base and harness override dispatch, "
        "argument value, invocation order, repetition, null receiver failure, and "
        "unchanged neighbors")
