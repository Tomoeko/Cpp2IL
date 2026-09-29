"""Independent behavior oracle for Boolean parameter stores through an array field."""

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
    expected = [{
        "kind": "default-construction", "prefix0": 0, "prefix1": 0,
        "prefix2": 0, "valuesNull": True, "otherValuesNull": True,
        "neighbor": 0,
    }]
    for label, missing_owner, initial in SCENARIOS:
        length = len(initial) if initial is not None else 0
        indices = (MINIMUM, -1, 0, 1, length - 1, length, MAXIMUM)
        for index in indices:
            for value in (False, True):
                before = list(initial) if initial is not None else None
                after = list(initial) if initial is not None else None
                if missing_owner or initial is None:
                    failure = "System.NullReferenceException"
                elif index < 0 or index >= length:
                    failure = "System.IndexOutOfRangeException"
                else:
                    failure = "none"
                    after[index] = value
                expected.append({
                    "case": label, "index": index, "value": value,
                    "failure": failure, "before": before, "after": after,
                    "aliasAfter": after if not missing_owner else None,
                    "sameFieldReference": not missing_owner,
                    "aliasSharesArray": not missing_owner and initial is not None,
                    "otherSame": None if missing_owner else True,
                    "otherAfter": None if missing_owner else [False, True, False],
                    "prefix0": None if missing_owner else -41,
                    "prefix1": None if missing_owner else 73,
                    "prefix2": None if missing_owner else -109,
                    "neighbor": None if missing_owner else 307,
                    "aliasNeighbor": None if missing_owner else -19,
                })
    return expected


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "field-parameter-boolean-array-store",
        observations(), 2,
        "An instance Boolean[] field store from an Int32 index and Boolean parameter; "
        "ordered receiver/array-null and unsigned bounds failures, value bits, "
        "array aliases and unchanged neighbors")
