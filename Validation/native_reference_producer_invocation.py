"""Independent state oracle for reference-return producers and checked Boolean consumers."""

from behavior_oracle import verify_report


CASES = ("success", "source-null", "result-null", "holder-null", "provider-alias", "node-alias",
         "replacement-null", "source-owner-null", "replacement-result-null", "replacement-owner-null",
         "negative-counters", "counter-overflow")


def increment(value):
    return ((value + 1 + (1 << 31)) & 0xffffffff) - (1 << 31)


def node(calls=0, flag=False):
    return {"calls": calls, "flag": flag}


def provider(reads=0, result=None):
    return {"reads": reads, "result": result, "owner": None}


def holder(source=None, replacement=None, value=0, neighbor=0):
    return {"source": source, "replacement": replacement, "value": value, "neighbor": neighbor,
            "beforeCount": 0, "flag": False}


def identity(value, names):
    if value is None:
        return "null"
    return next((name for name, candidate in names if value is candidate), "other")


def holder_state(owner, source, replacement):
    if owner is None:
        return None
    return {name: identity(owner[name], (("source", source), ("replacement", replacement)))
            for name in ("source", "replacement")} | {
        name: owner[name] for name in ("value", "neighbor", "beforeCount", "flag")}


def provider_state(value, owner, first, second):
    return {"reads": value["reads"], "result": identity(value["result"], (("first", first), ("second", second))),
            "ownerNull": value["owner"] is None, "ownerIsHolder": owner is not None and value["owner"] is owner}


def invoke(owner, value):
    if owner is None or owner["source"] is None:
        return "System.NullReferenceException"
    source = owner["source"]
    source["reads"] = increment(source["reads"])
    producer_owner = source["owner"]
    if producer_owner is None:
        return "System.NullReferenceException"
    producer_owner.update(source=producer_owner["replacement"], value=producer_owner["neighbor"],
                          flag=not producer_owner["flag"], beforeCount=increment(producer_owner["beforeCount"]))
    # Return from the original producer object after replacement; the consumer
    # is checked only after all producer and helper effects have completed.
    target = source["result"]
    if target is None:
        return "System.NullReferenceException"
    target.update(calls=increment(target["calls"]), flag=value)
    return "none"


def record(operation, kind, value, owner, source, replacement, first, second):
    exception = invoke(owner, value)
    return {"kind": kind, "operation": operation, "value": value, "exception": exception,
            "holder": holder_state(owner, source, replacement), "providerAlias": source is replacement,
            "nodeAlias": first is second, "source": provider_state(source, owner, first, second),
            "replacement": provider_state(replacement, owner, first, second),
            "first": first.copy(), "second": second.copy()}


def observations():
    prefix = "NativeReferenceProducerInvocationFixture."
    rows = [
        {"kind": "declarations", "methods": 8, "fields": 11, "sourceType": prefix + "Provider",
         "replacementType": prefix + "Provider", "resultType": prefix + "Node", "ownerType": prefix + "InvocationHolder",
         "signatures": {"Node.SetFlag": ["System.Void", "System.Boolean"], "Provider.ReadTarget": [prefix + "Node"],
                        "InvocationHolder.ReplaceFields": ["System.Void"],
                        "InvocationHolder.Forward": ["System.Void", "System.Boolean"],
                        "InvocationHolder.ForwardSnapshot": ["System.Void", "System.Boolean"]}},
        {"kind": "defaults", "holder": holder_state(holder(), None, None),
         "provider": provider_state(provider(), None, None, None), "node": node()},
    ]
    for operation in range(2):
        for value in (False, True):
            for kind in CASES:
                first = node(7, not value)
                second = first if kind == "node-alias" else node(-13, value)
                source = provider(7, None if kind == "result-null" else first)
                replacement = source if kind == "provider-alias" else provider(
                    -11, None if kind == "replacement-result-null" else second)
                owner = None if kind == "holder-null" else holder(
                    None if kind == "source-null" else source,
                    None if kind == "replacement-null" else replacement, 2147483647, 43)
                source["owner"] = replacement["owner"] = owner
                if kind == "source-owner-null": source["owner"] = None
                if kind == "replacement-owner-null": replacement["owner"] = None
                if kind in ("negative-counters", "counter-overflow"):
                    count = -2147483648 if kind == "negative-counters" else 2147483647
                    source["reads"] = first["calls"] = owner["beforeCount"] = count
                rows.append(record(operation, kind, value, owner, source, replacement, first, second))

            first, second = node(7, not value), node(-13, value)
            source, replacement = provider(7, first), provider(-11, second)
            owner = holder(None, replacement, 2147483647, 43)
            source["owner"] = replacement["owner"] = owner
            rows.append(record(operation, "reuse-source-null", value, owner, source, replacement, first, second))
            owner["source"], source["result"] = source, None
            rows.append(record(operation, "reuse-result-null", value, owner, source, replacement, first, second))
            owner["source"], source["result"] = source, first
            rows.append(record(operation, "reuse-success", value, owner, source, replacement, first, second))
            rows.append(record(operation, "repeat", not value, owner, source, replacement, first, second))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-reference-producer-invocation", observations(), 8,
                         "captured distinct-type provider, returned reference checked after ordered producer/helper effects, "
                         "old-provider result despite source replacement, incoming Boolean values, null order, provider/node aliases, "
                         "counter wrap and repeated replacement reuse; finite observed states")
