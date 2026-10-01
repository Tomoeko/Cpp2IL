"""Independent state oracle for sequential null-checked invocation composition."""

from behavior_oracle import verify_report


def node(value, flag, neighbor):
    return {"calls": 0, "value": value, "flag": flag, "neighbor": neighbor}


def holder():
    return {"beforeCount": 0, "afterCount": 0, "neighbor": 43, "targetNull": False,
            "targetFirst": True, "targetSecond": False, "replacementNull": False}


def expected(operation, kind, integer, boolean, first, second, owner, alias=False):
    exception = "none"
    if owner is None or owner["targetNull"]:
        exception = "System.NullReferenceException"
    else:
        # The first query or ping is an observable effect even when the second
        # receiver fails. Its result controls only the conditional setter.
        first["calls"] += 1
        if operation == 7:
            first["calls"] += 1
        if operation != 3 or first["flag"]:
            destination = first
            if operation in (4, 5):
                absent = owner["replacementNull"]
                owner.update(targetNull=absent, targetFirst=alias and not absent,
                             targetSecond=not absent)
                owner["beforeCount"] += 1
                if operation == 4:
                    destination = None if absent else second
            elif operation == 6:
                destination = None if owner["replacementNull"] else second
            if destination is None:
                exception = "System.NullReferenceException"
            else:
                destination["calls"] += 1
                if operation == 1:
                    destination["flag"] = boolean
                else:
                    destination["value"] = integer
                    if operation == 2:
                        destination["flag"] = boolean
                if operation in (4, 5):
                    owner["afterCount"] += 1
    return {"kind": kind, "operation": operation, "integer": integer, "boolean": boolean,
            "exception": exception, "holder": None if owner is None else owner.copy(),
            "alias": alias, "first": first.copy(), "second": second.copy()}


def observations():
    rows = [
        {"kind": "declarations", "methods": 15, "fields": 9, "queryReturn": "System.Boolean",
         "pairInteger": "System.Int32", "pairBoolean": "System.Boolean"},
        {"kind": "defaults", "holder": {"beforeCount": 0, "afterCount": 0, "neighbor": 0,
         "targetNull": True, "targetFirst": False, "targetSecond": False, "replacementNull": True},
         "node": node(0, False, 0)},
    ]
    for operation in range(8):
        for kind, first_null, second_null, absent, alias, first_flag, integer, boolean in (
                ("success", False, False, False, False, True, 29, True),
                ("query-false", False, False, False, False, False, -41, False),
                ("first-null", True, False, False, False, True, 47, True),
                ("second-null", False, True, False, False, True, -53, False),
                ("holder-null", False, False, True, False, True, 59, True),
                ("alias", False, False, False, True, True, -2147483648, False)):
            first = node(17, first_flag, 31)
            second = first if alias else node(-19, False, -37)
            owner = None if absent else holder()
            if owner is not None:
                owner.update(targetNull=first_null, targetFirst=not first_null,
                             targetSecond=alias and not first_null, replacementNull=second_null)
            rows.append(expected(operation, kind, integer, boolean, first, second, owner, alias))
        first, second, owner = node(17, True, 31), node(-19, False, -37), holder()
        owner.update(targetNull=True, targetFirst=False)
        rows.append(expected(operation, "reuse-failure", 2147483647, False, first, second, owner))
        owner.update(targetNull=False, targetFirst=True, targetSecond=False)
        rows.append(expected(operation, "reuse-success", -2147483648, True, first, second, owner))
        owner.update(targetNull=False, targetFirst=True, targetSecond=False)
        rows.append(expected(operation, "repeat", 2147483647, False, first, second, owner))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-sequential-null-invocation", observations(), 15,
                         "sequential and conditional calls, scalar widths, receiver reloads and snapshots, "
                         "replacement stores, distinct and aliased receivers, ordered null failures and reuse; "
                         "finite observed states")
