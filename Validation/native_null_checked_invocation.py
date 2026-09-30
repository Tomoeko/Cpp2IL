"""Independent typed state oracle for native invocation receiver snapshots."""

from behavior_oracle import verify_report


# Each column is observable: producer calls, pre-call effects, post-call effects,
# result channel, and the node effect. The table is independent of native lifting.
CONTRACT = (
    (1, 0, 0, "integer", "read"),
    (1, 0, 0, None, "integer-argument"),
    (1, 0, 0, None, "boolean-argument"),
    (1, 0, 0, None, "true"),
    (1, 0, 0, None, "negative-literal"),
    (0, 1, 0, "integer", "read"),
    (0, 1, 1, None, "integer-argument"),
    (0, 1, 1, None, "boolean-argument"),
    (0, 1, 0, None, "false"),
    (0, 1, 0, "integer", "replace-snapshot"),
    (1, 0, 0, "boolean", "read"),
)


def node(value, flag, neighbor):
    return {"calls": 0, "value": value, "flag": flag, "neighbor": neighbor}


def holder():
    return {"producerCount": 0, "beforeCount": 0, "afterCount": 0, "neighbor": 43,
            "targetNull": False, "targetFirst": True, "targetSecond": False, "replacementNull": False}


def expected(operation, kind, integer, boolean, first, second, owner, target_null=False, alias=False):
    producer, before, after, result_kind, effect = CONTRACT[operation]
    result = None
    exception = "none"
    if owner is None:
        exception = "System.NullReferenceException"
    else:
        owner["producerCount"] += producer
        owner["beforeCount"] += before
        if effect == "replace-snapshot":
            owner.update(targetNull=False, targetFirst=alias, targetSecond=True)
        if target_null:
            exception = "System.NullReferenceException"
        else:
            first["calls"] += 1
            if effect == "integer-argument":
                first["value"] = integer
            elif effect == "negative-literal":
                first["value"] = -7
            elif effect == "boolean-argument":
                first["flag"] = boolean
            elif effect == "true":
                first["flag"] = True
            elif effect == "false":
                first["flag"] = False
            owner["afterCount"] += after
            if result_kind is not None:
                result = first["value" if result_kind == "integer" else "flag"]
    return {"kind": kind, "operation": operation, "integer": integer, "boolean": boolean,
            "exception": exception, "result": result, "holder": None if owner is None else owner.copy(),
            "alias": alias, "first": first.copy(), "second": second.copy()}


def observations():
    rows = [
        {"kind": "declarations", "methods": 18, "fields": 10, "producerReturn": True,
         "booleanParameter": "System.Boolean", "integerParameter": "System.Int32"},
        {"kind": "defaults", "holder": {"producerCount": 0, "beforeCount": 0, "afterCount": 0,
         "neighbor": 0, "targetNull": True, "targetFirst": False, "targetSecond": False,
         "replacementNull": True}, "node": node(0, False, 0)},
    ]
    for operation in range(11):
        for kind, null, absent, alias, integer, boolean in (
                ("success", False, False, False, 29, True),
                ("target-null", True, False, False, -41, False),
                ("holder-null", False, True, False, 47, True),
                ("alias", False, False, True, -2147483648, False)):
            first = node(17, True, 31)
            second = first if alias else node(-19, False, -37)
            owner = None if absent else holder()
            if owner is not None:
                owner.update(targetNull=null, targetFirst=not null, targetSecond=alias and not null)
            rows.append(expected(operation, kind, integer, boolean, first, second, owner, null, alias))
        first, second, owner = node(17, True, 31), node(-19, False, -37), holder()
        owner.update(targetNull=True, targetFirst=False)
        rows.append(expected(operation, "reuse-failure", 2147483647, False, first, second, owner, True))
        owner.update(targetNull=False, targetFirst=True, targetSecond=False)
        rows.append(expected(operation, "reuse-success", -2147483648, True, first, second, owner))
        owner.update(targetNull=False, targetFirst=True, targetSecond=False)
        rows.append(expected(operation, "repeat", 2147483647, False, first, second, owner))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-null-checked-invocation", observations(), 18,
                         "producer effects, typed scalar/literal calls, receiver snapshots across field replacement, "
                         "aliases, ordered null failures and repeated reuse; finite observed states")
