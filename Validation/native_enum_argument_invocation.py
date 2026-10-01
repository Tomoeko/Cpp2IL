"""Oracle for signed enum arguments, declaration identity and ordered null failure."""

from behavior_oracle import verify_report


ENUM = "NativeEnumArgumentInvocationFixture.Selector"


def observations():
    rows = [{"kind": "declarations", "types": 3, "methods": 8, "fields": 6,
             "underlying": "System.Int32", "backing": "System.Int32",
             "acceptParameter": ENUM, "forwardParameter": ENUM, "sequenceParameter": ENUM,
             "constants": [{"name": name, "type": ENUM, "literal": True, "value": value}
                           for name, value in (("Zero", 0), ("Positive", 17), ("Negative", -7))]},
            {"kind": "defaults", "state": {"calls": 0, "last": 0}}]
    scenarios = (("zero", 0), ("negative", -29), ("maximum", 2147483647),
                 ("minimum", -2147483648), ("first-null", 17), ("second-null", 17),
                 ("both-null", 0), ("alias", 17))
    for operation in range(6):
        for name, value in scenarios:
            first = None if name in ("first-null", "both-null") else {"calls": 10, "last": 31}
            second = first if name == "alias" else (
                None if name in ("second-null", "both-null") else {"calls": 20, "last": -37})
            exception = "none"
            if first is None:
                exception = "System.NullReferenceException"
            elif operation < 5:
                first["calls"] += 1
                first["last"] = (0, 17, -7, 1234567, value)[operation]
            else:
                first["calls"] += 1
                first["last"] = value
                if second is None:
                    exception = "System.NullReferenceException"
                else:
                    second["calls"] += 1
                    second["last"] = -7
                    first["calls"] += 1
                    first["last"] = 1234567
            rows.append({"kind": "invocation", "operation": operation, "case": name, "value": value,
                         "exception": exception, "alias": first is second, "first": first, "second": second})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-enum-argument-invocation", observations(), 8,
                         "full eight-method assembly; signed I4 enum declarations, named and unnamed constants, "
                         "forwarded arguments, aliased receivers and effect order before null failure")
