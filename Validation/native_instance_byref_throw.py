"""Finite oracle for throw-only methods preserving byref arguments and aliases."""

from behavior_oracle import verify_report


def observations():
    thrown = "System.NotSupportedException"
    rows = [{"kind": "single", "ownerNull": False, "initial": value,
             "value": value, "result": None, "exception": thrown}
            for value in (-23, 0, -(2**31), 2**31 - 1)]
    rows.append({"kind": "single", "ownerNull": True, "initial": 17,
                 "value": 17, "result": None, "exception": "System.NullReferenceException"})
    for kind, first, second, third, first_second, first_third in (
            ("nulls", None, None, None, True, True),
            ("distinct", [5], [7, 11], [13], False, False),
            ("aliases", [-3, 17], [-3, 17], [-3, 17], True, True),
            ("null-owner", [-3, 17], None, [-3, 17], False, True)):
        rows.append({"kind": kind, "result": None,
                     "exception": "System.NullReferenceException" if kind == "null-owner" else thrown,
                     "referencesUnchanged": True, "first": first, "second": second, "third": third,
                     "firstSecondAlias": first_second, "firstThirdAlias": first_third})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-instance-byref-throw", observations(), 3,
                         "full three-method assembly; exact throws, unchanged byrefs and aliased list contents")
