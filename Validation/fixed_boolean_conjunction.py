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


def at_one(values):
    return [not values[0], values[0]] if values else values


AT_ONE_CASES = tuple((kind, at_one(first), at_one(second), aliased, owner)
                     for kind, first, second, aliased, owner in CASES) + (
    ("first-short", [True], None, False, True),
    ("second-short", [True, False], [False], False, True),
    ("skip-second-short", [False, True], [False], False, True),
    ("alias-short", [False], [False], True, True),
)


def observations():
    expected = [{"kind": "declarations", "methods": 4, "fields": 3,
                 "baseType": "FixedBooleanConjunctionFixture.EmptyBase`1", "baseArgument": "System.Int32",
                 "baseGenericParameters": 1, "baseFields": 0, "baseConstructorParameters": 0,
                 "firstType": "System.Boolean[]", "secondType": "System.Boolean[]",
                 "sentinelType": "System.Int32", "atZeroParameters": 0, "atOneParameters": 0,
                 "atZeroReturn": "System.Boolean", "atOneReturn": "System.Boolean"}]
    for method, index, cases in (("BothFalseAtZero", 0, CASES), ("BothFalseAtOne", 1, AT_ONE_CASES)):
        for kind, first, second, aliased, owner in cases:
            result = None
            exception = "none"
            if not owner or first is None:
                exception = "System.NullReferenceException"
            elif len(first) <= index:
                exception = "System.IndexOutOfRangeException"
            elif first[index]:
                result = False
            elif second is None:
                exception = "System.NullReferenceException"
            elif len(second) <= index:
                exception = "System.IndexOutOfRangeException"
            else:
                result = not second[index]
            expected.append({
                "method": method, "kind": kind, "result": result, "exception": exception,
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
        path, stage, version, "fixed-boolean-conjunction", observations(), 4,
        "two ordered Boolean-array reads at indices zero and one, short circuit, null and bounds failures, "
        "contrasting neighboring elements, aliases and unchanged fields; fieldless constructed generic base and declaration identities")
