"""Independent state oracle for two typed scalar invocation arguments."""

from behavior_oracle import verify_report


# Each column is observable: producer calls, pre-call effects, post-call effects,
# result channel, and the node effect. The table is independent of native lifting.
CONTRACT = (
    (1, 0, 0, None, "pair"),
    (1, 0, 0, None, "pair"),
    (1, 0, 0, None, "integers"),
    (1, 0, 0, None, "flags"),
    (0, 1, 1, None, "pair"),
    (0, 1, 1, None, "pair"),
    (0, 1, 0, None, "replace-snapshot"),
    (0, 1, 0, None, "literals"),
)


def node(value, flag, neighbor):
    return {"calls": 0, "value": value, "flag": flag, "neighbor": neighbor, "secondFlag": False}


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
            if effect in ("pair", "replace-snapshot"):
                first.update(value=integer, flag=boolean)
            elif effect == "integers":
                first.update(value=integer, neighbor=-7)
            elif effect == "flags":
                first.update(flag=boolean, secondFlag=True)
            elif effect == "literals":
                first.update(value=-7, flag=False)
            owner["afterCount"] += after
            if result_kind is not None:
                result = first["value" if result_kind == "integer" else "flag"]
    return {"kind": kind, "operation": operation, "integer": integer, "boolean": boolean,
            "exception": exception, "result": result, "holder": None if owner is None else owner.copy(),
            "alias": alias, "first": first.copy(), "second": second.copy()}


def observations():
    rows = [
        {"kind": "declarations", "methods": 15, "fields": 11, "producerReturn": True,
         "booleanParameter": "System.Boolean", "integerParameter": "System.Int32"},
        {"kind": "defaults", "holder": {"producerCount": 0, "beforeCount": 0, "afterCount": 0,
         "neighbor": 0, "targetNull": True, "targetFirst": False, "targetSecond": False,
         "replacementNull": True}, "node": node(0, False, 0)},
    ]
    for operation in range(8):
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
    return verify_report(path, stage, version, "native-scalar-pair-invocation", observations(), 15,
                         "producer effects, two ordered typed scalar/literal arguments, receiver snapshots across field replacement, "
                         "aliases, ordered null failures and repeated reuse; finite observed states")
