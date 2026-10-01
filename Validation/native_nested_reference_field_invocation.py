"""Independent identity, state and exception oracle for nested reference arguments."""

from behavior_oracle import verify_report


CASES = ("success", "source-null", "target-null", "both-null", "payload-null",
         "holder-null", "payload-alias", "counter-negative", "counter-overflow", "target-second")


def record(kind, owner, first, second, sources):
    before = None if owner is None else owner.copy()
    exception = "none"
    if owner is None or owner["source"] == "null":
        exception = "System.NullReferenceException"
    else:
        payload = sources[owner["source"]]
        if owner["target"] == "null":
            exception = "System.NullReferenceException"
        else:
            node = first if owner["target"] == "first" else second
            node.update(calls=((node["calls"] + 1 + (1 << 31)) & 0xffffffff) - (1 << 31), value=payload)
    return {"kind": kind, "exception": exception, "before": before,
            "holder": None if owner is None else owner.copy(), "first": first.copy(), "second": second.copy(),
            "firstSource": sources["first"], "secondSource": sources["second"]}


def observations():
    prefix = "NativeNestedReferenceFieldInvocationFixture."
    rows = [{"kind": "declarations", "methods": 6, "fields": 5,
             "targetType": prefix + "Node", "sourceType": prefix + "SourceOwner",
             "payloadType": prefix + "Payload", "valueType": prefix + "Payload",
             "signatures": {"Node.Accept": ["System.Void", prefix + "Payload"], "InvocationHolder.Forward": ["System.Void"]}},
            {"kind": "defaults", "targetNull": True, "sourceNull": True, "payloadNull": True,
             "node": {"calls": 0, "value": "null"}}]
    sources = {"first": "first", "second": "second"}
    for kind in CASES:
        sources["first"] = "null" if kind == "payload-null" else "first"
        first = {"calls": 7, "value": "first" if kind == "payload-alias" else "second"}
        second = {"calls": -13, "value": "first"}
        if kind == "counter-negative": first["calls"] = -(1 << 31)
        if kind == "counter-overflow": first["calls"] = (1 << 31) - 1
        owner = None if kind == "holder-null" else {
            "target": "null" if kind in ("target-null", "both-null") else "second" if kind == "target-second" else "first",
            "source": "null" if kind in ("source-null", "both-null") else "first"}
        rows.append(record(kind, owner, first, second, sources))
    first, second = {"calls": 17, "value": "second"}, {"calls": -13, "value": "first"}
    owner = {"target": "first", "source": "null"}
    rows.append(record("reuse-source-null", owner, first, second, sources))
    owner.update(source="first", target="null")
    rows.append(record("reuse-target-null", owner, first, second, sources))
    owner["target"] = "first"
    sources["first"] = "null"
    rows.append(record("reuse-payload-null", owner, first, second, sources))
    sources["first"] = "first"
    rows.append(record("reuse-first", owner, first, second, sources))
    owner.update(source="second", target="second")
    rows.append(record("reuse-second", owner, first, second, sources))
    rows.append(record("repeat-second", owner, first, second, sources))
    other = {"target": "first", "source": "first"}
    rows.append(record("shared-source-first", other, first, second, sources))
    owner["source"] = "first"
    sources["first"] = "second"
    rows.append(record("shared-source-second", owner, first, second, sources))
    other["target"] = "second"
    rows.append(record("shared-target-other", other, first, second, sources))
    sources["first"] = "null"
    rows.append(record("shared-target-null-payload", owner, first, second, sources))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-nested-reference-field-invocation", observations(), 6,
                         "Nested reference capture, source-owner and consumer null guards, payload identity, "
                         "signed counter wrap, aliasing and fresh reads on reuse")
