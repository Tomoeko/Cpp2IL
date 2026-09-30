"""Independent typed state oracle for an owner toggle before a checked call."""

from behavior_oracle import verify_report


PROFILE = "native-boolean-toggle-invocation"
NAMESPACE = "NativeBooleanToggleInvocationFixture."


def holder(flag=False, target_null=True):
    return {"flag": flag, "targetNull": target_null, "targetNode": not target_null}


def record(kind, initial, owner, other, node):
    exception = "none"
    if owner is None:
        exception = "System.NullReferenceException"
    else:
        owner["flag"] = not owner["flag"]
        if owner["targetNull"]:
            exception = "System.NullReferenceException"
        else:
            node["calls"] += 1
            node["flag"] = owner["flag"]
    return {"kind": kind, "initialFlag": initial, "exception": exception,
            "holder": None if owner is None else owner.copy(),
            "otherHolder": None if other is None else other.copy(),
            "sharedTarget": owner is not None and other is not None and not owner["targetNull"] and
                            not other["targetNull"], "node": node.copy()}


def observations():
    rows = [
        {"kind": "declarations", "methods": 4, "fields": 4, "parameterType": "System.Boolean",
         "toggleParameters": 0, "flagType": "System.Boolean", "targetType": NAMESPACE + "Node"},
        {"kind": "defaults", "holder": holder(), "node": {"calls": 0, "flag": False}},
    ]
    for initial in (False, True):
        for kind, owner in (("success", holder(initial, False)),
                            ("target-null", holder(initial)), ("holder-null", None)):
            rows.append(record(kind, initial, owner, None, {"calls": 7, "flag": initial}))
        node, owner = {"calls": 7, "flag": initial}, holder(initial)
        rows.append(record("reuse-failure", initial, owner, None, node))
        owner.update(targetNull=False, targetNode=True)
        rows.append(record("reuse-success", initial, owner, None, node))
        rows.append(record("repeat", initial, owner, None, node))
        node = {"calls": 7, "flag": initial}
        first, second = holder(initial, False), holder(not initial, False)
        rows.append(record("shared-target-first", initial, first, second, node))
        rows.append(record("shared-target-second", initial, second, first, node))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, PROFILE, observations(), 4,
                         "exact Boolean owner store before target-null failure; checked callee counter/flag "
                         "effects, null owner, failed/successful reuse, repeat and distinct owners of one target; "
                         "all recorded states and declaration identities")
