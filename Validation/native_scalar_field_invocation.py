"""Independent state oracle for captured scalar field invocation arguments."""

from behavior_oracle import verify_report


# Argument source, literal Boolean override, pre-call replacement and post-call effect.
CONTRACT = (
    ("field", False, False, False),
    ("incoming", None, False, False),
    ("field", None, False, False),
    ("field", None, False, False),
    ("field", None, False, True),
    ("captured", None, True, True),
    ("field", None, True, True),
)


def node(value, flag):
    return {"calls": 0, "value": value, "flag": flag}


def holder(value=0, flag=False, neighbor=0):
    return {"value": value, "flag": flag, "neighbor": neighbor,
            "beforeCount": 0, "afterCount": 0, "targetNull": True,
            "targetFirst": False, "targetSecond": False, "replacementNull": True}


def expected(operation, kind, integer, first, second, owner, alias=False):
    source, boolean_override, replace, after = CONTRACT[operation]
    exception = "none"
    if owner is None:
        exception = "System.NullReferenceException"
    else:
        # All calls retain the receiver read before replacement. Only operation 5
        # also retains the scalar field reads across that observable effect.
        target_null = owner["targetNull"]
        captured_value, captured_flag = owner["value"], owner["flag"]
        if replace:
            owner.update(value=owner["neighbor"], flag=not owner["flag"],
                         beforeCount=owner["beforeCount"] + 1,
                         targetNull=owner["replacementNull"],
                         targetFirst=alias and not owner["replacementNull"],
                         targetSecond=not owner["replacementNull"])
        if target_null:
            exception = "System.NullReferenceException"
        else:
            value = integer if source == "incoming" else captured_value if source == "captured" else owner["value"]
            flag = captured_flag if source == "captured" else owner["flag"]
            if boolean_override is not None:
                flag = boolean_override
            first.update(calls=first["calls"] + 1, value=value, flag=flag)
            owner["afterCount"] += int(after)
    return {"kind": kind, "operation": operation, "integer": integer,
            "exception": exception, "result": None, "holder": None if owner is None else owner.copy(),
            "alias": alias, "first": first.copy(), "second": second.copy()}


def observations():
    rows = [
        {"kind": "declarations", "methods": 12, "fields": 10,
         "booleanField": "System.Boolean", "integerField": "System.Int32",
         "booleanParameter": "System.Boolean", "integerParameter": "System.Int32"},
        {"kind": "defaults", "holder": holder(), "node": node(0, False)},
    ]
    for operation in range(len(CONTRACT)):
        for kind, null, absent, alias, replacement_null, integer, flag in (
                ("success", False, False, False, False, 29, True),
                ("target-null", True, False, False, False, -41, False),
                ("holder-null", False, True, False, False, 47, True),
                ("alias", False, False, True, False, -2147483648, False),
                ("replacement-null", False, False, False, True, 2147483647, True)):
            first = node(17, True)
            second = first if alias else node(-19, False)
            owner = None if absent else holder(integer, flag, 43)
            if owner is not None:
                owner.update(targetNull=null, targetFirst=not null, targetSecond=alias and not null,
                             replacementNull=replacement_null)
            rows.append(expected(operation, kind, integer, first, second, owner, alias))
        first, second, owner = node(17, True), node(-19, False), holder(2147483647, False, 43)
        owner["replacementNull"] = False
        rows.append(expected(operation, "reuse-failure", -53, first, second, owner))
        owner.update(targetNull=False, targetFirst=True, targetSecond=False)
        rows.append(expected(operation, "reuse-success", -2147483648, first, second, owner))
        owner.update(targetNull=False, targetFirst=True, targetSecond=False)
        rows.append(expected(operation, "repeat", 2147483647, first, second, owner))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-scalar-field-invocation", observations(), 12,
                         "ordered Int32/Boolean field and incoming arguments, captures before/after replacement effects, "
                         "saved receivers, aliases, null failure ordering and repeated reuse; finite observed states")
