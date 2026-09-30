"""Independent typed observations for ordered conditional call-result tails."""

from behavior_oracle import verify_report


def node(ready, value, integer, neighbor):
    return {"ready": ready, "value": value, "predicateCount": 0, "applyCount": 0,
            "lastInteger": integer, "neighbor": neighbor}


def expected_row(operation, kind, ready, first_null=False, second_null=False,
                 alias=False, owner_null=False, predicate_count=0):
    first = node(ready, False, -11, 17)
    second = first if alias else node(not ready if not kind.startswith("reuse-") else False, True, 13, -19)
    first["predicateCount"] = predicate_count
    calls = None if owner_null else 1
    exception = "System.NullReferenceException" if owner_null or first_null else "none"
    if not owner_null and not first_null:
        first["predicateCount"] += 1
        taken = ready if operation in (0, 1, 4) else not ready
        if taken:
            calls = 2
            if second_null:
                exception = "System.NullReferenceException"
            else:
                second["applyCount"] += 1
                if operation == 4:
                    second["lastInteger"] = 0
                else:
                    second["value"] = operation in (0, 2)
    return {"kind": kind, "operation": operation, "exception": exception, "ownerNull": owner_null,
            "producerCount": calls, "ownerNeighbor": None if owner_null else 23,
            "firstNull": None if owner_null else first_null, "secondNull": None if owner_null else second_null,
            "firstSameSecond": alias, "first": first.copy(), "second": second.copy()}


def observations():
    rows = [
        {"kind": "declarations", "methods": 12, "fields": 10, "producerProtected": True,
         "callerVirtual": True, "predicateReturn": "System.Boolean", "booleanParameter": "System.Boolean",
         "integerParameter": "System.Int32", "baseIdentity": True},
        {"kind": "defaults", "firstNull": True, "secondNull": True, "producerCount": 0, "ownerNeighbor": 0,
         "ready": False, "value": False, "predicateCount": 0, "applyCount": 0, "lastInteger": 0, "nodeNeighbor": 0},
    ]
    for operation in range(5):
        rows.extend((
            expected_row(operation, "ready", True),
            expected_row(operation, "not-ready", False),
            expected_row(operation, "first-null", True, first_null=True),
            expected_row(operation, "second-null-ready", True, second_null=True),
            expected_row(operation, "second-null-not-ready", False, second_null=True),
            expected_row(operation, "alias-ready", True, alias=True),
            expected_row(operation, "alias-not-ready", False, alias=True),
            expected_row(operation, "null-owner", True, owner_null=True),
            expected_row(operation, "reuse-failure", operation not in (2, 3), second_null=True),
            expected_row(operation, "reuse-success", operation not in (2, 3), predicate_count=1),
        ))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "conditional-call-result-tail", observations(), 12,
                         "finite predicate branches, repeated producer effects, distinct/aliased receivers, "
                         "ordered first/second null failures and reuse; not whole-program equivalence")
