"""Independent typed state oracle for derived receivers of inherited setters."""

from behavior_oracle import verify_report


NAMESPACE = "NativeDerivedReceiverInvocationFixture."


def node(value=0, flag=False, extra=0):
    return {"calls": 0, "value": value, "flag": flag, "extra": extra}


def holder(target_null=True, target_first=False, target_second=False):
    return {"targetNull": target_null, "targetFirst": target_first, "targetSecond": target_second,
            "targetRuntimeType": None if target_null else NAMESPACE + "DerivedNode"}


def expected(operation, kind, integer, boolean, first, second, owner, alias=False):
    exception = "none"
    if owner is None or owner["targetNull"]:
        exception = "System.NullReferenceException"
    else:
        target = first if owner["targetFirst"] else second
        target["calls"] += 1
        target["value" if operation == 0 else "flag"] = integer if operation == 0 else boolean
    return {"kind": kind, "operation": operation, "integer": integer, "boolean": boolean,
            "exception": exception, "holder": None if owner is None else owner.copy(), "alias": alias,
            "first": first.copy(), "second": second.copy()}


def observations():
    rows = [
        {"kind": "declarations", "methods": 7, "fields": 5, "baseType": NAMESPACE + "BaseNode",
         "targetType": NAMESPACE + "DerivedNode", "integerDeclaringType": NAMESPACE + "BaseNode",
         "booleanDeclaringType": NAMESPACE + "BaseNode", "extraDeclaringType": NAMESPACE + "DerivedNode",
         "integerParameter": "System.Int32", "booleanParameter": "System.Boolean"},
        {"kind": "defaults", "holder": holder(), "base": {"calls": 0, "value": 0, "flag": False},
         "derived": node()},
    ]
    for operation in range(2):
        for kind, target_null, holder_null, alias, use_second, integer, boolean in (
                ("success", False, False, False, False, 29, True),
                ("target-null", True, False, False, False, -41, False),
                ("holder-null", False, True, False, False, 47, True),
                ("distinct-target", False, False, False, True, -2147483648, False),
                ("alias", False, False, True, False, 2147483647, True)):
            first = node(17, False, 31)
            second = first if alias else node(-19, True, -37)
            owner = None if holder_null else holder(target_null, not target_null and (not use_second or alias),
                                                    not target_null and (use_second or alias))
            rows.append(expected(operation, kind, integer, boolean, first, second, owner, alias))
        first, second, owner = node(17, False, 31), node(-19, True, -37), holder()
        rows.append(expected(operation, "reuse-failure", 2147483647, False, first, second, owner))
        owner.update(targetNull=False, targetFirst=True, targetRuntimeType=NAMESPACE + "DerivedNode")
        rows.append(expected(operation, "reuse-success", -2147483648, True, first, second, owner))
        rows.append(expected(operation, "repeat", 2147483647, False, first, second, owner))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-derived-receiver-invocation", observations(), 7,
                         "declared Derived receiver and inherited Base setter identity, exact Int32/Boolean "
                         "arguments, signed boundaries, distinct/aliased derived nodes, preserved derived fields, "
                         "ordered null failures and repeated reuse; finite observed states")
