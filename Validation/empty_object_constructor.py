"""Independent observations for two empty direct-Object constructors."""

from behavior_oracle import verify_report


def _cell(kind, number, flag, payload_null, same_first, same_second):
    return {"kind": kind, "number": number, "flag": flag,
            "payloadNull": payload_null, "sameFirst": same_first,
            "sameSecond": same_second}


def _sibling(kind, count, label):
    return {"kind": kind, "count": count, "label": label, "sameOther": False}


def observations():
    return [
        _cell("first-default", 0, False, True, True, False),
        _cell("second-default", 0, False, True, False, True),
        {**_cell("first-changed", 73, True, False, True, False),
         "payloadSame": True},
        _cell("second-unchanged", 0, False, True, False, True),
        _cell("third-default", 0, False, True, False, False),
        _sibling("sibling-default", 0, None),
        _sibling("sibling-changed", (1 << 63) - 1, "changed"),
        _sibling("other-sibling-unchanged", 0, None),
    ]


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "empty-object-constructor", observations(), 2,
        "Two direct-Object empty constructors, default field state, distinct allocations, "
        "and unchanged sibling instances")
