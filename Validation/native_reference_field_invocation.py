"""Independent state and exception-order oracle for reference-field call arguments."""

from behavior_oracle import verify_report


CASES = ("success", "target-null", "source-null", "holder-null", "payload-alias",
         "counter-negative", "counter-overflow")


def increment(value):
    return ((value + 1 + (1 << 31)) & 0xffffffff) - (1 << 31)


def record(kind, initial_flag, owner, first, second):
    before = None if owner is None else owner.copy()
    exception = "none"
    if owner is None:
        exception = "System.NullReferenceException"
    else:
        target = owner["target"]
        owner.update(flag=True, markerBits="00000000")
        if target == "null":
            exception = "System.NullReferenceException"
        else:
            node = first if target == "first" else second
            node.update(calls=increment(node["calls"]), value=owner["source"])
    return {"kind": kind, "initialFlag": initial_flag, "exception": exception, "before": before,
            "holder": None if owner is None else owner.copy(), "first": first.copy(), "second": second.copy()}


def observations():
    prefix = "NativeReferenceFieldInvocationFixture."
    rows = [{"kind": "declarations", "methods": 5, "fields": 6,
             "targetType": prefix + "Node", "sourceType": prefix + "Payload", "valueType": prefix + "Payload",
             "signatures": {"Node.Accept": ["System.Void", prefix + "Payload"], "InvocationHolder.Forward": ["System.Void"]}},
            {"kind": "defaults", "holder": {"target": "null", "source": "null", "flag": False, "markerBits": "00000000"},
             "node": {"calls": 0, "value": "null"}}]
    for flag in (False, True):
        for kind in CASES:
            first = {"calls": 7, "value": "first" if kind == "payload-alias" else "second"}
            second = {"calls": -13, "value": "first"}
            if kind == "counter-negative": first["calls"] = -(1 << 31)
            if kind == "counter-overflow": first["calls"] = (1 << 31) - 1
            owner = None if kind == "holder-null" else {
                "target": "null" if kind == "target-null" else "first",
                "source": "null" if kind == "source-null" else "first", "flag": flag, "markerBits": "c0000000"}
            rows.append(record(kind, flag, owner, first, second))
    first, second = {"calls": 17, "value": "second"}, {"calls": -13, "value": "first"}
    owner = {"target": "null", "source": "first", "flag": False, "markerBits": "80000000"}
    rows.append(record("reuse-target-null", False, owner, first, second))
    owner.update(target="first", source="null")
    rows.append(record("reuse-source-null", False, owner, first, second))
    owner["source"] = "first"
    rows.append(record("reuse-first", False, owner, first, second))
    owner.update(target="second", source="second")
    rows.append(record("reuse-second", False, owner, first, second))
    rows.append(record("repeat-second", False, owner, first, second))
    owner.update(target="first", source="null")
    rows.append(record("replace-source-null", False, owner, first, second))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-reference-field-invocation", observations(), 5,
                         "Checked captured target after Boolean/Single stores, reference-field argument, null and alias "
                         "preservation, signed counter wrap, repeated calls and source/target replacement")
