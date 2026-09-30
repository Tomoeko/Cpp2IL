"""Independent behavior oracle for equal native bodies owned by distinct managed types."""

from behavior_oracle import verify_report


MINIMUM = -(1 << 31)
MAXIMUM = (1 << 31) - 1
SCENARIOS = (
    ("null-owner", True, None),
    ("null-array", False, None),
    ("empty", False, ()),
    ("single-false", False, (False,)),
    ("single-true", False, (True,)),
    ("mixed", False, (True, False, True)),
)


def observations():
    expected = []
    for label, missing_owner, initial in SCENARIOS:
        length = len(initial) if initial is not None else 0
        indices = (MINIMUM, -1, 0, 1, length - 1, length, MAXIMUM)
        for index in indices:
            for owner in ("first", "second"):
                before = list(initial) if initial is not None else None
                after = list(initial) if initial is not None else None
                if missing_owner or initial is None:
                    exception = "System.NullReferenceException"
                elif index < 0 or index >= length:
                    exception = "System.IndexOutOfRangeException"
                else:
                    exception = "none"
                    after[index] = False
                expected.append({
                    "owner": owner, "case": label, "index": index,
                    "exception": exception, "before": before, "after": after,
                    "aliasAfter": None if missing_owner else after,
                    "sameFieldReference": not missing_owner,
                    "aliasSharesArray": not missing_owner and initial is not None,
                    "prefix0": None if missing_owner else -41,
                    "prefix1": None if missing_owner else 73,
                    "suffix": None if missing_owner else 307,
                    "aliasSuffix": None if missing_owner else -19,
                })
    return expected


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "folded-boolean-array-store", observations(), 5,
        "Two distinct managed owners of one folded native Boolean[] false store; "
        "null and bounds exits, aliases, unchanged neighbors")
