"""Independent observations for bounded nullable delegate-field invocation."""

from behavior_oracle import verify_report


def _row(kind, exception, instance_count, static_count, trace,
         direct_callback_null, padded_callback_null):
    return {
        "kind": kind, "exception": exception,
        "instanceCount": instance_count, "staticCount": static_count,
        "trace": trace, "directNeighbor": True, "paddedNeighbor": True,
        "paddingIntact": True,
        "directCallbackNull": direct_callback_null,
        "paddedCallbackNull": padded_callback_null,
    }


def observations():
    return [
        _row("constructors:defaults", "none", 0, 0, "", True, True),
        _row("direct:null", "none", 0, 0, "", True, True),
        _row("direct:instance", "none", 1, 0, "I", False, True),
        _row("direct:repeat", "none", 2, 0, "II", False, True),
        _row("direct:static", "none", 2, 1, "IIS", False, True),
        _row("direct:multicast", "none", 3, 2, "IISIS", False, True),
        _row("padded:null", "none", 3, 2, "IISIS", False, True),
        _row("padded:instance", "none", 4, 2, "IISISI", False, False),
        _row("padded:multicast", "none", 5, 3, "IISISISI", False, False),
        _row("padded:throws", "System.InvalidOperationException", 6, 3,
             "IISISISIT", False, False),
        _row("direct:null-again", "none", 6, 3, "IISISISIT", True, False),
        _row("direct:null-owner", "System.NullReferenceException", 6, 3,
             "IISISISIT", True, False),
        _row("padded:null-owner", "System.NullReferenceException", 6, 3,
             "IISISISIT", True, False),
    ]


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "nullable-delegate-field-tail", observations(), 4,
        "Two nullable zero-argument delegate-field tails with direct and padded "
        "field displacements, constructor defaults, null owner/callback cases, instance/static and "
        "multicast ordering, repeated calls, side effects, exception propagation, "
        "and unchanged neighboring fields")
