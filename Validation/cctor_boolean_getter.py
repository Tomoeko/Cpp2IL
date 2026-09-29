"""Independent behavior oracle for an instance Boolean getter on a class with a cctor."""

from behavior_oracle import verify_report


def observations():
    rows = [{"kind": "default", "flag": False, "neighbor": False,
             "counter": 0, "first": False, "repeat": False}]
    for flag in (False, True):
        for neighbor in (False, True):
            rows.append({"kind": "read", "flag": flag,
                         "neighbor": neighbor, "counter": 37,
                         "first": flag, "repeat": flag,
                         "flagAfter": flag, "neighborAfter": neighbor,
                         "counterAfter": 37})
    rows.append({"kind": "null", "failure": "System.NullReferenceException"})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "cctor-boolean-getter",
                         observations(), 3,
                         "Explicit empty class constructor, distinct Boolean fields, "
                         "repeated reads, unchanged neighbors and null receiver")
