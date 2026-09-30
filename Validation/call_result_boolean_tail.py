"""Independent effects for Boolean arguments forwarded through an effectful producer."""

from behavior_oracle import verify_report


def observations():
    result = [
        {"kind": "declarations", "methods": 10, "fields": 4,
         "producerReturn": "CallResultBooleanTailFixture.TailNode", "instanceParameter": "System.Boolean",
         "staticReceiver": "CallResultBooleanTailFixture.TailOwner", "staticParameter": "System.Boolean"},
        {"kind": "defaults", "ownerNodeNull": True, "ownerCount": 0, "nodeValue": False, "nodeCount": 0},
    ]
    producer = alias_producer = count = 0
    current = True

    def record(kind, operation, argument, *, missing_result=False, missing_owner=False, alias_owner=False):
        nonlocal producer, alias_producer, count, current
        if not missing_owner:
            if alias_owner:
                alias_producer += 1
            else:
                producer += 1
        if not missing_owner and not missing_result:
            count += 1
            current = False if operation in (0, 3) else True if operation in (1, 4) else argument
        result.append({
            "kind": kind, "operation": operation, "argument": argument,
            "exception": "System.NullReferenceException" if missing_owner or missing_result else "none",
            "ownerNull": missing_owner,
            "producerCount": None if missing_owner else alias_producer if alias_owner else producer,
            "nodeNull": None if missing_owner else missing_result,
            "nodeSameWitness": None if missing_owner else not missing_result,
            "value": current, "applyCount": count, "aliasSameWitness": True,
            "aliasCount": producer if alias_owner else alias_producer,
        })

    for operation in range(6):
        record("success-" + str(operation), operation, True)
        record("repeat-" + str(operation), operation, False)
    record("alias-true", 5, True, alias_owner=True)
    for operation in range(6):
        record("null-result-" + str(operation), operation, True, missing_result=True)
    for operation in range(6):
        record("null-owner-" + str(operation), operation, True, missing_owner=True)
    record("reuse-false", 2, False)
    record("reuse-true", 5, True)
    return result


def verify(path, stage, version):
    return verify_report(path, stage, version, "call-result-boolean-tail", observations(), 10,
                         "literal and preserved parameter arguments, producer effects, aliasing and ordered null failures")
