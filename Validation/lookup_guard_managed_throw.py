"""Independent lookup guard, message formatting and effect-order oracle."""

from behavior_oracle import verify_report


def _row(kind, key, result=None, exception="none", message=None,
         initializations=0, producer=1, lookup=1, hidden_getter=0):
    return {"kind": kind, "key": key, "result": result,
            "exception": exception, "message": message, "parameter": None,
            "sameString": True if exception == "none" else None,
            "initializations": initializations, "producerCalls": producer,
            "lookupCalls": lookup, "lastKey": key if lookup else 0,
            "hiddenGetterCalls": hidden_getter}


def observations():
    argument = "System.ArgumentException"
    null = "System.NullReferenceException"
    return [
        _row("cold", 37, "first", initializations=1),
        _row("warm", -2, "first"),
        _row("null-text", 0),
        _row("empty-text", 1, ""),
        _row("alias-text", 2, "ab"),
        _row("minimum-key", -(2**31), "minimum"),
        _row("maximum-key", 2**31 - 1, "maximum"),
        _row("absent-zero", 0, exception=argument, message="Missing key: 0"),
        _row("absent-minimum", -(2**31), exception=argument,
             message="Missing key: -2147483648"),
        _row("absent-maximum", 2**31 - 1, exception=argument,
             message="Missing key: 2147483647"),
        _row("null-producer", 17, exception=null, lookup=0),
        _row("lookup-throw", -17, exception="System.InvalidOperationException",
             message="lookup failure"),
        _row("post-throw", 19, "reused"),
        _row("absent-negative", -17, exception=argument, message="Missing key: -17"),
        _row("absent-custom-culture", -17, exception=argument, message="Missing key: ~17"),
        _row("null-caller", 23, exception=null, producer=0, lookup=0),
        _row("after-null-caller", 29, "after-null"),
        _row("property-value", 31, "property"),
        _row("property-null-text", 0),
        _row("property-absent", 0, exception=argument, message="Missing property key: 0"),
        _row("property-null-producer", 33, exception=null, lookup=0),
        _row("inherited-value", 41, "base"),
        _row("inherited-null-text", 0),
        _row("inherited-alias", 43, "ab"),
        _row("inherited-absent", 0, exception=argument, message="Missing inherited key: 0"),
        _row("inherited-custom-culture", -17, exception=argument,
             message="Missing inherited key: ~17"),
        _row("inherited-null-producer", 47, exception=null, lookup=0),
        _row("inherited-lookup-throw", -47, exception="System.InvalidOperationException",
             message="lookup failure"),
        _row("inherited-null-caller", 53, exception=null, producer=0, lookup=0),
        _row("hidden-getter-control", 59, "hidden", producer=0, lookup=0, hidden_getter=1),
    ]


def verify(path, stage, version):
    return verify_report(path, stage, version, "lookup-guard-managed-throw",
                         observations(), 4,
                         "managed lookup, exact exception and message, current-culture formatting, "
                         "receiver null behavior, string identity, inherited member binding and ordered effects")
