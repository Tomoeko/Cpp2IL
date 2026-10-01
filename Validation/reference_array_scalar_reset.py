"""Independent partial-effect and raw-bit oracle for reference-array reset loops."""

import copy

from behavior_oracle import verify_report


BOOLEAN_CASES = ("holder-null", "null-array", "empty", "one", "three", "null-first",
                 "null-middle", "null-last", "alias")
SINGLE_CASES = ("holder-null", "null-array", "empty", "one", "short", "exact", "long",
                "null-first", "null-middle", "null-last", "null-after-limit", "alias", "tail-alias")
SINGLE_BITS = (0x80000000, 0x7FC01234, 0x00000001, 0x80000001,
               0x7F800000, 0xFF800000, 0x3F800000, 0xFFC05678)
MARKER_BITS = 0xFFC02468


def signed(bits):
    return bits - (1 << 32) if bits & 0x80000000 else bits


def flags(length):
    return [{"identity": index, "active": True, "neighbor": 501 + index} for index in range(length)]


def values(length):
    return [{"identity": index, "valueBits": signed(SINGLE_BITS[index % len(SINGLE_BITS)]),
             "neighbor": -601 - index} for index in range(length)]


def owner():
    return {"markerBits": signed(MARKER_BITS), "flags": flags(3), "values": values(8)}


def scenario(method, kind):
    if kind == "holder-null":
        return None
    result = owner()
    name = "flags" if method == "active" else "values"
    length = {"empty": 0, "one": 1, "three": 3, "short": 6, "exact": 7}.get(kind, 3 if method == "active" else 8)
    result[name] = flags(length) if method == "active" else values(length)
    if kind == "null-array": result[name] = None
    if kind == "null-first": result[name][0] = None
    if kind == "null-middle": result[name][1 if method == "active" else 3] = None
    if kind == "null-last": result[name][2 if method == "active" else 6] = None
    if kind == "null-after-limit": result[name][7] = None
    if kind == "alias": result[name][1] = result[name][0]
    if kind == "tail-alias": result[name][7] = result[name][0]
    return result


def record(rows, method, kind, holder):
    before = copy.deepcopy(holder)
    exception = "none"
    if holder is None:
        exception = "System.NullReferenceException"
    else:
        if method == "active": holder["markerBits"] = 0
        items = holder["flags" if method == "active" else "values"]
        if items is None:
            exception = "System.NullReferenceException"
        else:
            for index in range(len(items) if method == "active" else 7):
                if index >= len(items):
                    exception = "System.IndexOutOfRangeException"
                    break
                item = items[index]
                if item is None:
                    exception = "System.NullReferenceException"
                    break
                item["active" if method == "active" else "valueBits"] = False if method == "active" else 0
    rows.append({"kind": kind, "method": method, "exception": exception, "before": before,
                 "after": copy.deepcopy(holder), "flagsSame": holder is not None, "valuesSame": holder is not None})


def observations():
    prefix = "ReferenceArrayScalarResetFixture."
    rows = [{"kind": "declarations", "methods": 5, "fields": 7,
             "signatures": {"ResetHolder.ResetActive": ["System.Void"], "ResetHolder.ResetValues": ["System.Void"]},
             "fieldTypes": {"BooleanEntry.Active": "System.Boolean", "BooleanEntry.Neighbor": "System.Int32",
                            "SingleEntry.Value": "System.Single", "SingleEntry.Neighbor": "System.Int32",
                            "ResetHolder.Marker": "System.Single", "ResetHolder.Flags": prefix + "BooleanEntry[]",
                            "ResetHolder.Values": prefix + "SingleEntry[]"}},
            {"kind": "defaults", "markerBits": 0, "flagsNull": True, "valuesNull": True,
             "active": False, "booleanNeighbor": 0, "valueBits": 0, "singleNeighbor": 0}]
    for method, cases in (("active", BOOLEAN_CASES), ("values", SINGLE_CASES)):
        for kind in cases: record(rows, method, kind, scenario(method, kind))
    holder = owner()
    holder["flags"] = None
    record(rows, "active", "reuse-null-array", holder)
    holder["flags"] = flags(3)
    saved = holder["flags"][1]
    holder["flags"][1] = None
    record(rows, "active", "reuse-null-middle", holder)
    holder["flags"][1] = saved
    record(rows, "active", "reuse-repaired", holder)
    record(rows, "active", "reuse-repeat", holder)
    holder = owner()
    holder["values"] = values(6)
    record(rows, "values", "reuse-short", holder)
    holder["values"] = values(7)
    saved = holder["values"][6]
    holder["values"][6] = None
    record(rows, "values", "reuse-null-last", holder)
    holder["values"][6] = saved
    record(rows, "values", "reuse-repaired", holder)
    record(rows, "values", "reuse-repeat", holder)
    first, second = owner(), owner()
    second["flags"] = first["flags"]
    second["values"] = first["values"]
    record(rows, "active", "shared-first", first)
    first["flags"][1]["active"] = True
    record(rows, "active", "shared-second", second)
    record(rows, "values", "shared-first", first)
    first["values"][1]["valueBits"] = signed(0x80000001)
    record(rows, "values", "shared-second", second)
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "reference-array-scalar-reset", observations(), 5,
                         "Complete reference-array scalar reset loops, partial effects, null and unsigned bounds "
                         "exceptions, raw Single bits, aliases, adjacent fields and reuse")
