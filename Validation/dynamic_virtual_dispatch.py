"""Independent arithmetic and effect-order oracle for virtual scalar calls."""

from behavior_oracle import verify_report

INPUTS = (-(1 << 31), -(1 << 31) + 1, -65536, -32769, -32768, -129, -128, -1,
          0, 1, 2, 127, 128, 255, 256, 32767, 32768, 65535, 65536,
          (1 << 31) - 2, (1 << 31) - 1)

KINDS = ("base", "override", "inherited", "shadow")
ROUTES = ("virtual", "virtual-store")


def signed32(value):
    value &= (1 << 32) - 1
    return value - (1 << 32) if value >= (1 << 31) else value


def row(owner, route, value, counter, reference_null, receiver_null=False, marker_null=False):
    if route == "concrete-shadow":
        result, increment = signed32(value + 11), 8
    elif owner == "base":
        result, increment = signed32(value + 3), 1
    else:
        result, increment = signed32(value - 5), 2
    stores = route.endswith("store")
    failed = receiver_null or stores and marker_null
    return {
        "kind": "dispatch", "owner": owner, "route": route, "input": value,
        "counterBefore": counter, "referenceNull": reference_null,
        "receiverNull": receiver_null, "markerNull": marker_null,
        "value": None if failed else result,
        "failure": "System.NullReferenceException" if failed else "none",
        "counterAfter": None if receiver_null else signed32(counter + increment),
        "neighborAfter": None if receiver_null else -137, "referenceSame": True,
        "markerAfter": None if marker_null else result if stores and not failed else -101,
        "markerNeighborAfter": None if marker_null else 307, "markerReferenceSame": True,
    }


def observations():
    expected = [{"kind": "constructor", "owner": owner, "counter": 0,
                 "neighbor": 0, "referenceNull": True} for owner in KINDS]
    expected.append({"kind": "constructor", "owner": "marker", "value": 0,
                     "neighbor": 0, "referenceNull": True})
    for value in INPUTS:
        for counter in (-3, 0, (1 << 31) - 1):
            for reference_null in (False, True):
                for route in ROUTES:
                    for owner in KINDS:
                        expected.append(row(owner, route, value, counter, reference_null))
                        if route.endswith("store"):
                            expected.append(row(owner, route, value, counter, reference_null, marker_null=True))
                expected.append(row("shadow", "concrete-shadow", value, counter, reference_null))
    for value in INPUTS:
        for route in ROUTES:
            for marker_null in (False, True):
                expected.append(row("missing", route, value, 0, False, True, marker_null))
    return expected


def verify(path, stage, version):
    return verify_report(path, stage, version, "dynamic-virtual-dispatch", observations(), 10,
                         "Ten concrete class bodies; dynamic virtual scalar tail and post-call store, "
                         "inherited override versus concrete shadow, signed32 overflow and ordered null effects")
