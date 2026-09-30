"""Independent integer/storage oracle for typed value-type field addresses."""

from behavior_oracle import verify_report


SAMPLES = (0, 1, -1, 2, -2, 9223372036854775807, -9223372036854775808,
           9223372036854775806, -9223372036854775807, 2147483647, -2147483648,
           1099511627776, -1099511627776, 1311768467463790320)
SMALL_SAMPLES = (0, 1, -1, 2147483647, -2147483648, 2147483646, -2147483647,
                 32767, -32768, 65535, -65536)
LONG_METHODS = ("AdvanceFirst", "AdvanceSecond", "ReadFirst", "AdvanceTwice", "GuardedAdvance")
NULL_METHODS = ("AdvanceFirst", "AdvanceSecond", "AdvanceSmall", "ResetFirst", "ReadFirst",
                "GuardedAdvance", "AdvanceTwice")


def wrap(value, bits):
    if type(value) is not int or bits not in (32, 64):
        raise ValueError("An integer and exact supported storage width are required")
    return (value + (1 << (bits - 1))) % (1 << bits) - (1 << (bits - 1))


def initial():
    return {"first": 19, "second": -23, "small": 31, "enabled": True}


def apply(method, state, value):
    if method in ("AdvanceFirst", "GuardedAdvance", "AdvanceTwice"):
        if method == "GuardedAdvance" and not state["enabled"]:
            return 0
        state["first"] = wrap(state["first"] + value, 64)
        if method == "AdvanceTwice":
            state["first"] = wrap(state["first"] + value, 64)
        return state["first"]
    if method == "AdvanceSecond":
        state["second"] = wrap(state["second"] + value, 64)
        return state["second"]
    if method == "AdvanceSmall":
        state["small"] = wrap(state["small"] + wrap(value, 32), 32)
        return state["small"]
    if method == "ReadFirst":
        return state["first"]
    if method == "ResetFirst":
        state["first"] = 0
        return None
    raise ValueError("Unknown fixture method")


def record(kind, method, state, value, enabled):
    state["enabled"] = enabled
    copy_first = state["first"]
    result = apply(method, state, value)
    return dict(state, kind=kind, method=method, input=value, exception="none", result=result,
                copyFirst=copy_first, sameOwner=True, sameArray=True, neighbor=initial())


def declarations():
    ns = "TypedFieldAddressFixture."
    i64, i32 = "System.Int64", "System.Int32"
    return {
        "kind": "declarations", "methods": 12, "fields": 6,
        "counterSize": 8, "smallSize": 4, "counterSequential": True, "smallSequential": True,
        "counterValueType": i64, "smallValueType": i32, "firstType": ns + "CounterCell",
        "secondType": ns + "CounterCell", "smallType": ns + "SmallCell", "enabledType": "System.Boolean",
        "signatures": {
            "CounterCell.Advance": [i64, i64], "CounterCell.Reset": ["System.Void"], "CounterCell.Read": [i64],
            "SmallCell.Advance": [i32, i32], "StorageOwner.AdvanceFirst": [i64, i64],
            "StorageOwner.AdvanceSecond": [i64, i64], "StorageOwner.AdvanceSmall": [i32, i32],
            "StorageOwner.ResetFirst": ["System.Void"], "StorageOwner.ReadFirst": [i64],
            "StorageOwner.GuardedAdvance": [i64, i64], "StorageOwner.AdvanceTwice": [i64, i64],
        },
    }


def observations():
    rows = [declarations(), {"kind": "defaults", "first": 0, "second": 0, "small": 0, "enabled": False}]
    for value in SAMPLES:
        for method in LONG_METHODS:
            if method == "GuardedAdvance":
                rows.append(record("operation", method, initial(), value, False))
            rows.append(record("operation", method, initial(), value, True))
    rows.append(record("operation", "ResetFirst", initial(), 0, True))
    rows.extend(record("operation", "AdvanceSmall", initial(), value, True) for value in SMALL_SAMPLES)
    rows.extend(dict(initial(), kind="null-owner", method=method, exception="System.NullReferenceException")
                for method in NULL_METHODS)
    state = initial()
    for method, value, enabled in (
            ("AdvanceFirst", 9223372036854775807, True), ("AdvanceSecond", -17, True),
            ("AdvanceSmall", 2147483647, True), ("ReadFirst", 0, True),
            ("GuardedAdvance", 3, False), ("GuardedAdvance", 3, True),
            ("AdvanceTwice", -5, True), ("ResetFirst", 0, True)):
        rows.append(record("reuse", method, state, value, enabled))
    for method, assigned, value in (("AdvanceFirst", 36, 3), ("AdvanceTwice", 41, -5)):
        state = initial()
        state["first"] = assigned
        result = apply(method, state, value)
        rows.append(dict(state, kind="ref-alias", method=method, assigned=assigned, input=value,
                         result=result, slotAfter=state["first"], copyFirst=19, sameOwner=True,
                         sameArray=True, neighbor=initial()))
    if len(rows) != 115:
        raise ValueError("Observation denominator changed")
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "typed-field-address", observations(), 12,
                         "typed value-type field address calls, signed4/8-byte storage wrap, guarded no-op, repeated/aliased mutation, neighboring fields and null-owner faults")
