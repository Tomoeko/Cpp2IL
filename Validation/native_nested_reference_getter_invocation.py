"""Independent protected-storage getter, reference identity and exception oracle."""

from behavior_oracle import verify_report


PROFILES = {
    "base": ("native-nested-reference-getter-invocation", "NativeNestedReferenceGetterInvocationFixture", 7, 5, 1, 4),
    "folded": ("native-nested-reference-getter-folded-invocation", "NativeNestedReferenceGetterFoldedInvocationFixture", 9, 6, 2, 5),
    "ambiguous": ("native-nested-reference-getter-ambiguous-invocation", "NativeNestedReferenceGetterAmbiguousInvocationFixture", 8, 5, 2, 4),
}
CASES = ("success", "source-null", "target-null", "both-null", "payload-null",
         "holder-null", "payload-alias", "counter-negative", "counter-overflow", "target-second")


def declarations(variant):
    _, assembly, methods, fields, properties, types = PROFILES[variant]
    prefix = assembly + "."
    signatures = {"SourceOwner.get_Payload": [prefix + "Payload"],
                  "Node.Accept": ["System.Void", prefix + "Payload"], "InvocationHolder.Forward": ["System.Void"]}
    field_types = {"SourceOwner._payload": prefix + "Payload", "Node.Calls": "System.Int32",
                   "Node.Value": prefix + "Payload", "InvocationHolder.Target": prefix + "Node",
                   "InvocationHolder.Source": prefix + "SourceOwner"}
    property_types = {"SourceOwner.Payload": prefix + "Payload"}
    if variant == "folded":
        signatures["MirrorSource.get_Payload"] = [prefix + "Payload"]
        field_types["MirrorSource._payload"] = prefix + "Payload"
        property_types["MirrorSource.Payload"] = prefix + "Payload"
    elif variant == "ambiguous":
        signatures["SourceOwner.get_OtherPayload"] = [prefix + "Payload"]
        property_types["SourceOwner.OtherPayload"] = prefix + "Payload"
    return {"kind": "declarations", "methods": methods, "fields": fields, "properties": properties, "types": types,
            "signatures": signatures, "fieldTypes": field_types,
            "fieldAccess": {name: "Family" if name.endswith("._payload") else "Public" for name in field_types},
            "propertyTypes": property_types, "readOnlyProperties": True}


def getter(kind, value, throws=False):
    return {"kind": kind, "exception": "System.NullReferenceException" if throws else "none",
            "payload": "not-returned" if throws else value}


def record(kind, owner, first, second, sources, variant):
    before = None if owner is None else owner.copy()
    exception = "none"
    if owner is None or owner["source"] == "null":
        exception = "System.NullReferenceException"
    elif owner["target"] == "null":
        exception = "System.NullReferenceException"
    else:
        node = first if owner["target"] == "first" else second
        node.update(calls=((node["calls"] + 1 + (1 << 31)) & 0xffffffff) - (1 << 31), value=sources[owner["source"]])
    row = {"kind": kind, "exception": exception, "before": before,
           "holder": None if owner is None else owner.copy(), "first": first.copy(), "second": second.copy(),
           "firstSource": sources["first"], "secondSource": sources["second"]}
    if variant == "folded": row["mirrorSource"] = "second"
    return row


def observations(variant="base"):
    rows = [declarations(variant), {"kind": "defaults", "targetNull": True, "sourceNull": True, "payloadNull": True,
                                     "node": {"calls": 0, "value": "null"}}]
    if variant == "folded": rows[1]["mirrorPayloadNull"] = True
    rows.extend((getter("getter-default", "null"), getter("getter-first", "first"), getter("getter-second", "second"),
                 getter("getter-null", "null", True)))
    if variant == "folded":
        rows.extend((getter("mirror-getter-default", "null"), getter("mirror-getter-first", "first"),
                     getter("mirror-getter-second", "second"), getter("mirror-getter-null", "null", True)))
    elif variant == "ambiguous":
        rows.extend((getter("other-getter-first", "first"), getter("other-getter-second", "second"),
                     getter("other-getter-null", "null", True)))
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
        rows.append(record(kind, owner, first, second, sources, variant))
    first, second = {"calls": 17, "value": "second"}, {"calls": -13, "value": "first"}
    owner = {"target": "first", "source": "null"}
    rows.append(record("reuse-source-null", owner, first, second, sources, variant))
    owner.update(source="first", target="null")
    rows.append(record("reuse-target-null", owner, first, second, sources, variant))
    owner["target"] = "first"
    sources["first"] = "null"
    rows.append(record("reuse-payload-null", owner, first, second, sources, variant))
    sources["first"] = "first"
    rows.append(record("reuse-first", owner, first, second, sources, variant))
    owner.update(source="second", target="second")
    rows.append(record("reuse-second", owner, first, second, sources, variant))
    rows.append(record("repeat-second", owner, first, second, sources, variant))
    other = {"target": "first", "source": "first"}
    rows.append(record("shared-source-first", other, first, second, sources, variant))
    owner["source"] = "first"
    sources["first"] = "second"
    rows.append(record("shared-source-second", owner, first, second, sources, variant))
    other["target"] = "second"
    rows.append(record("shared-target-other", other, first, second, sources, variant))
    sources["first"] = "null"
    rows.append(record("shared-target-null-payload", owner, first, second, sources, variant))
    return rows


def verify_variant(path, stage, version, variant):
    profile, _, methods, _, _, _ = PROFILES[variant]
    return verify_report(path, stage, version, profile, observations(variant), methods,
                         "Readonly protected-storage getters, explicit source and consumer null guards, reference identity, "
                         "signed counter wrap, aliases and fresh payload reads on reuse")


def verify(path, stage, version):
    return verify_variant(path, stage, version, "base")


def verify_folded(path, stage, version):
    return verify_variant(path, stage, version, "folded")


def verify_ambiguous(path, stage, version):
    return verify_variant(path, stage, version, "ambiguous")
