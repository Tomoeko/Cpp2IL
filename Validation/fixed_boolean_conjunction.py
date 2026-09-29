"""Independent oracle for two ordered fixed-index Boolean-array reads."""

from behavior_oracle import verify_report


CASES = (
    ("both-false", [False], [False], False, True),
    ("second-true", [False], [True], False, True),
    ("first-true", [True], [False], False, True),
    ("both-true", [True], [True], False, True),
    ("skip-second-null", [True], None, False, True),
    ("skip-second-empty", [True], [], False, True),
    ("first-null", None, [False], False, True),
    ("first-null-second-empty", None, [], False, True),
    ("first-empty", [], [False], False, True),
    ("first-empty-second-null", [], None, False, True),
    ("second-null", [False], None, False, True),
    ("second-empty", [False], [], False, True),
    ("alias-false", [False], [False], True, True),
    ("alias-true", [True], [True], True, True),
    ("owner-null", None, None, None, False),
)


def observations():
    expected = []
    for kind, first, second, aliased, owner in CASES:
        result = None
        exception = "none"
        if not owner or first is None:
            exception = "System.NullReferenceException"
        elif not first:
            exception = "System.IndexOutOfRangeException"
        elif first[0]:
            result = False
        elif second is None:
            exception = "System.NullReferenceException"
        elif not second:
            exception = "System.IndexOutOfRangeException"
        else:
            result = not second[0]
        expected.append({
            "kind": kind, "result": result, "exception": exception,
            "firstBefore": first, "secondBefore": second,
            "firstAfter": first, "secondAfter": second,
            "firstSecondSame": aliased,
            "firstFieldSame": True if owner else None,
            "secondFieldSame": True if owner else None,
            "sentinelBefore": 73 if owner else None,
            "sentinelAfter": 73 if owner else None,
        })
    return expected


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "fixed-boolean-conjunction", observations(), 2,
        "two ordered fixed Boolean-array reads, short circuit, null and bounds failures, aliases and unchanged fields")
