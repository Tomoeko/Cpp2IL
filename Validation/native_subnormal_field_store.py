"""Independent bit and state oracle for checked-call subnormal field stores."""

from behavior_oracle import verify_report


# Single and Double raw bits written by each wrapper; absent stores preserve the
# other marker. Operation 5 deliberately contains two observable native stores.
STORES = (
    (0x00000001, None),
    (0x007fffff, None),
    (0x80000001, None),
    (0x807fffff, None),
    (None, 0x0000000000000001),
    (0x80000001, 0x000fffffffffffff),
    (None, 0x8000000000000001),
    (None, 0x800fffffffffffff),
)


def signed(bits, width):
    return bits - (1 << width) if bits >> (width - 1) else bits


def node(calls=0, value=False):
    return {"calls": calls, "value": value}


def holder(single=0, double=0, target_null=True, target_first=False, target_second=False):
    return {"singleBits": single, "doubleBits": double, "targetNull": target_null,
            "targetFirst": target_first, "targetSecond": target_second}


def expected(operation, kind, value, first, second, owner, alias=False):
    exception = "none"
    if owner is None or owner["targetNull"]:
        exception = "System.NullReferenceException"
    else:
        first.update(calls=first["calls"] + 1, value=value)
        single, double = STORES[operation]
        if single is not None:
            owner["singleBits"] = signed(single, 32)
        if double is not None:
            owner["doubleBits"] = signed(double, 64)
    return {"kind": kind, "operation": operation, "value": value, "exception": exception,
            "holder": None if owner is None else owner.copy(), "alias": alias,
            "first": first.copy(), "second": second.copy()}


def observations():
    rows = [
        {"kind": "declarations", "methods": 11, "fields": 5, "singleType": "System.Single",
         "doubleType": "System.Double", "parameterType": "System.Boolean"},
        {"kind": "defaults", "holder": holder(), "node": node()},
    ]
    for operation in range(len(STORES)):
        for kind, target_null, holder_null, alias, value in (
                ("success", False, False, False, True),
                ("target-null", True, False, False, False),
                ("holder-null", False, True, False, True),
                ("alias", False, False, True, False),
                ("false-success", False, False, False, False)):
            first = node(7, not value)
            second = first if alias else node(-13, value)
            owner = None if holder_null else holder(0x3f800000, 0x3ff0000000000000,
                                                    target_null, not target_null, alias and not target_null)
            rows.append(expected(operation, kind, value, first, second, owner, alias))
        first, second = node(7, False), node(-13, True)
        owner = holder(0x3f800000, 0x3ff0000000000000)
        rows.append(expected(operation, "reuse-failure", True, first, second, owner))
        owner.update(targetNull=False, targetFirst=True)
        rows.append(expected(operation, "reuse-success", False, first, second, owner))
        rows.append(expected(operation, "repeat", True, first, second, owner))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-subnormal-field-store", observations(), 11,
                         "bit-exact signed minimum/maximum Single and Double subnormal field stores after checked "
                         "Boolean invocation, two-store composition, preserved neighbor bits, null failure ordering, "
                         "aliases and repeated reuse; finite observed states")
