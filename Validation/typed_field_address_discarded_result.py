"""Independent storage and signed-enum oracle for discarded field-call results."""

from behavior_oracle import verify_report


SAMPLES = (0, 1, -1, 2, -2, 9223372036854775807, -9223372036854775808,
           9223372036854775806, -9223372036854775807, 2147483647, -2147483648, 1099511627776)
MODES = (0, 1, -1, 2147483647, -2147483648)
METHODS = ("ResetFirstDiscard", "ResetBothDiscard", "ApplyDefault", "GuardedApplyDefault")


def wrap(value, bits):
    if type(value) is not int or bits not in (32, 64):
        raise ValueError("An integer and exact supported width are required")
    return (value + (1 << (bits - 1))) % (1 << bits) - (1 << (bits - 1))


def initial(value=19):
    return {"first": value, "second": -23, "enabled": True}


def apply(method, state):
    if method == "ResetFirstDiscard":
        state["first"] = 0
    elif method == "ResetBothDiscard":
        state["first"] = state["second"] = 0
    elif method == "ApplyDefault" or method == "GuardedApplyDefault" and state["enabled"]:
        state["first"] = wrap(state["first"] + 1, 64)
    elif method != "GuardedApplyDefault":
        raise ValueError("Unknown fixture method")


def record(kind, method, state, enabled):
    state["enabled"] = enabled
    before = state["first"]
    apply(method, state)
    return dict(state, kind=kind, method=method, input=before, exception="none", copyFirst=before,
                sameOwner=True, sameArray=True, neighbor=initial())


def declarations():
    ns = "TypedFieldAddressDiscardedResultFixture."
    result, mode = ns + "ResultCode", ns + "UpdateMode"
    return {
        "kind": "declarations", "methods": 7, "fields": 10,
        "cellSize": 8, "cellSequential": True, "cellValueType": "System.Int64",
        "firstType": ns + "ResultCell", "secondType": ns + "ResultCell", "enabledType": "System.Boolean",
        "resultUnderlying": "System.Int32", "modeUnderlying": "System.Int32",
        "resultIdle": 0, "resultChanged": 1, "modeDefault": 0, "modeAlternative": 1,
        "signatures": {
            "ResultCell.ResetAndReport": [result], "ResultCell.IncrementAndReport": [result, mode],
            **{"AddressResultOwner." + method: ["System.Void"] for method in METHODS},
        },
    }


def observations():
    rows = [declarations(), {"kind": "defaults", "first": 0, "second": 0, "enabled": False,
                             "resultDefault": 0, "modeDefault": 0}]
    for value in SAMPLES:
        for method in METHODS:
            if method == "GuardedApplyDefault":
                rows.append(record("operation", method, initial(value), False))
            rows.append(record("operation", method, initial(value), True))
        rows.append({"kind": "callee", "method": "ResetAndReport", "input": value, "mode": 0,
                     "result": 1, "value": 0, "copyValue": value, "exception": "none"})
        rows.extend({"kind": "callee", "method": "IncrementAndReport", "input": value, "mode": mode,
                     "result": wrap(mode, 32), "value": wrap(value + 1, 64), "copyValue": value,
                     "exception": "none"} for mode in MODES)
    rows.extend(dict(initial(), kind="null-owner", method=method, exception="System.NullReferenceException")
                for method in METHODS)
    state = initial()
    for method, enabled in (("ApplyDefault", True), ("GuardedApplyDefault", False),
                            ("GuardedApplyDefault", True), ("ResetFirstDiscard", True),
                            ("ApplyDefault", True), ("ResetBothDiscard", True)):
        rows.append(record("reuse", method, state, enabled))
    for method, assigned in (("ApplyDefault", 9223372036854775807), ("ResetBothDiscard", -37)):
        state = initial(assigned)
        apply(method, state)
        rows.append(dict(state, kind="ref-alias", method=method, assigned=assigned,
                         slotAfter=state["first"], copyFirst=19, sameOwner=True, sameArray=True, neighbor=initial()))
    if len(rows) != 146:
        raise ValueError("Observation denominator changed")
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "typed-field-address-discarded-result", observations(), 7,
                         "unused enum call results, literal-zero signed enum arguments, complete callee bodies, mutable field storage, wraparound, guards, aliases and null-owner faults")
