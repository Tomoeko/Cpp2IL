"""Oracle for original virtual dispatch, positive-zero bits and null receivers."""

from behavior_oracle import verify_report


PREFIX = "NativeVirtualScalarZeroLeafFixture."


def observations():
    getters = []
    for owner, abstract, new_slot, base in (("IZeroPair", True, True, "IZeroPair"),
                                            ("ZeroPair", False, True, "ZeroPair"),
                                            ("DerivedZero", False, False, "ZeroPair")):
        for name in ("First", "Second"):
            getters.append({"owner": PREFIX + owner, "property": name, "return": "System.Single",
                            "virtual": True, "abstract": abstract, "newSlot": new_slot,
                            "baseOwner": PREFIX + base, "parameters": 0})
    rows = [{"kind": "declarations", "methods": 12, "types": 5, "fields": 1, "properties": 6,
             "typeInitializers": 1, "markerReadonly": True, "getters": getters,
             "stringReturn": "System.Single", "stringParameters": 1, "stringParameter": "System.String",
             "stringVirtual": True, "stringFinal": True, "stringNested": True},
            {"kind": "marker", "phase": "before-use", "value": 17}]
    rows.extend({"kind": "alias", "case": name, "concreteSame": True, "interfaceSame": True}
                for name in ("base", "derived", "control"))
    for route in ("base-direct", "base-alias", "base-interface", "derived-direct", "derived-base",
                  "derived-interface", "control-base", "control-interface"):
        for name, control_bits in (("First", "3f800000"), ("Second", "c0000000")):
            rows.append({"kind": "dispatch", "route": route, "case": name,
                         "resultBits": control_bits if route.startswith("control") else "00000000",
                         "exception": "none", "marker": 17})
    for index in range(4):
        for route in ("concrete", "interface"):
            rows.append({"kind": "unused-string", "route": route, "case": str(index),
                         "resultBits": "00000000", "exception": "none", "marker": 17})
    for route in ("null-base", "null-derived", "null-interface"):
        for name in ("First", "Second"):
            rows.append({"kind": "null", "route": route, "case": name, "resultBits": "none",
                         "exception": "System.NullReferenceException", "marker": 17})
    for route in ("concrete-string", "interface-string"):
        for name in ("null", "value"):
            rows.append({"kind": "null", "route": route, "case": name, "resultBits": "none",
                         "exception": "System.NullReferenceException", "marker": 17})
    rows.append({"kind": "marker", "phase": "after-use", "value": 17})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-virtual-scalar-zero-leaf", observations(), 12,
                         "full twelve-method assembly; base/derived/interface dispatch, positive-zero bits, "
                         "unused String parameters, null receivers, nested type identities and retained initializer; "
                         "nonzero derived dispatch controls belong to the separate validation harness")
