"""Independent ordered-state oracle for captured consumers and Int32 producer calls."""

from behavior_oracle import verify_report


SCENARIOS = (
    ("success", 29), ("source-null", -41), ("target-null", 47), ("holder-null", -53),
    ("source-target-alias", -2147483648), ("target-replacement-alias", 2147483647),
    ("source-replacement-alias", 17), ("replacement-null", -19), ("source-owner-null", 23),
    ("minimum", -2147483648), ("maximum", 2147483647), ("counter-overflow", -31),
)


def increment(value):
    return ((value + 1 + (1 << 31)) & 0xffffffff) - (1 << 31)


def node(reads=0, calls=0, value=0):
    return {"reads": reads, "calls": calls, "value": value, "owner": None}


def holder(source=None, target=None, replacement=None, value=0, neighbor=0):
    return {"source": source, "target": target, "replacement": replacement,
            "value": value, "neighbor": neighbor, "flag": False, "beforeCount": 0}


def identity(value, source, target, replacement):
    if value is None:
        return "null"
    for name, candidate in (("source", source), ("target", target), ("replacement", replacement)):
        if value is candidate:
            return name
    return "other"


def holder_state(owner, source, target, replacement):
    if owner is None:
        return None
    return {name: identity(owner[name], source, target, replacement)
            for name in ("source", "target", "replacement")} | {
        name: owner[name] for name in ("value", "neighbor", "flag", "beforeCount")}


def node_state(value, owner):
    return {name: value[name] for name in ("reads", "calls", "value")} | {
        "ownerNull": value["owner"] is None,
        "ownerIsHolder": owner is not None and value["owner"] is owner}


def invoke(owner):
    # C# retains the consumer before evaluating the producer argument. Both
    # calls check their receivers only after their argument effects finish.
    if owner is None or owner["source"] is None:
        return "System.NullReferenceException"
    target = owner["target"]
    source = owner["source"]
    source["reads"] = increment(source["reads"])
    producer_owner = source["owner"]
    if producer_owner is None:
        return "System.NullReferenceException"
    producer_owner.update(target=producer_owner["replacement"], value=producer_owner["neighbor"],
                          flag=not producer_owner["flag"], beforeCount=increment(producer_owner["beforeCount"]))
    value = source["value"]
    if target is None:
        return "System.NullReferenceException"
    target.update(calls=increment(target["calls"]), value=value)
    return "none"


def record(operation, kind, owner, source, target, replacement):
    value = source["value"]
    exception = invoke(owner)
    return {"kind": kind, "operation": operation, "producerValue": value, "exception": exception,
            "holder": holder_state(owner, source, target, replacement),
            "sourceTargetAlias": source is target, "sourceReplacementAlias": source is replacement,
            "targetReplacementAlias": target is replacement, "source": node_state(source, owner),
            "target": node_state(target, owner), "replacement": node_state(replacement, owner)}


def observations():
    rows = [
        {"kind": "declarations", "methods": 7, "fields": 11,
         "ownerType": "NativeScalarProducerInvocationFixture.InvocationHolder",
         "sourceType": "NativeScalarProducerInvocationFixture.Node",
         "targetType": "NativeScalarProducerInvocationFixture.Node",
         "replacementType": "NativeScalarProducerInvocationFixture.Node",
         "signatures": {"Node.ReadValue": ["System.Int32"], "Node.SetValue": ["System.Void", "System.Int32"],
                        "InvocationHolder.ReplaceFields": ["System.Void"],
                        "InvocationHolder.Forward": ["System.Void"],
                        "InvocationHolder.ForwardSnapshot": ["System.Void"]}},
        {"kind": "defaults", "holder": holder_state(holder(), None, None, None), "node": node_state(node(), None)},
    ]
    for operation in range(2):
        for kind, value in SCENARIOS:
            source = node(7, 11, value)
            target = source if kind == "source-target-alias" else node(-13, 17, -19)
            replacement = target if kind == "target-replacement-alias" else (
                source if kind == "source-replacement-alias" else node(23, -29, 31))
            owner = None if kind == "holder-null" else holder(
                None if kind == "source-null" else source,
                None if kind == "target-null" else target,
                None if kind == "replacement-null" else replacement, 2147483647, 43)
            source["owner"] = None if kind == "source-owner-null" else owner
            if kind == "counter-overflow":
                source["reads"] = target["calls"] = owner["beforeCount"] = 2147483647
            rows.append(record(operation, kind, owner, source, target, replacement))

        source, target, replacement = node(7, 11, 2147483647), node(-13, 17, -19), node(23, -29, 31)
        owner = holder(None, target, replacement, 2147483647, 43)
        source["owner"] = owner
        rows.append(record(operation, "reuse-source-null", owner, source, target, replacement))
        owner.update(source=source, target=None)
        rows.append(record(operation, "reuse-target-null", owner, source, target, replacement))
        owner["target"], source["value"] = target, -2147483648
        rows.append(record(operation, "reuse-success", owner, source, target, replacement))
        source["value"] = 2147483647
        rows.append(record(operation, "repeat", owner, source, target, replacement))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-scalar-producer-invocation", observations(), 7,
                         "saved consumer identity before checked Int32 producer call, receiver replacement, "
                         "producer/callee side effects and null ordering, aliases, signed bounds and counter wrap; "
                         "finite observed states")
