"""Independent exact-target expectations for a guarded closed-generic call."""

from behavior_oracle import verify_report


DELTAS = (-(1 << 31), -(1 << 31) + 1, -65536, -129, -1, 0,
          1, 127, 65535, (1 << 31) - 2, (1 << 31) - 1)
COUNTERS = (-(1 << 31), -1, 0, 1, (1 << 31) - 1)
NULL_REFERENCE = "System.NullReferenceException"


def signed32(value):
    return ((value + (1 << 31)) % (1 << 32)) - (1 << 31)


def observations():
    expected = [
        {"kind": "constructor", "owner": "string", "counter": 0, "tagNull": True},
        {"kind": "constructor", "owner": "object", "counter": 0, "tagNull": True},
        {"kind": "constructor", "owner": "sink", "last": 0, "neighbor": 0,
         "referenceNull": True},
    ]
    for delta in DELTAS:
        for counter in COUNTERS:
            after = signed32(counter + delta)
            for tag_null in (False, True):
                for sink_null in (False, True):
                    expected.append({
                        "kind": "call", "delta": delta, "counterBefore": counter,
                        "tagNull": tag_null, "sinkNull": sink_null,
                        "value": None if sink_null else after,
                        "failure": NULL_REFERENCE if sink_null else "none",
                        "counterAfter": after,
                        "tagAfter": None if tag_null else "tag", "tagSame": True,
                        "sinkLastAfter": None if sink_null else after,
                        "sinkNeighborAfter": None if sink_null else 307,
                        "sinkReferenceSame": None if sink_null else True,
                    })
    for delta in DELTAS:
        for counter in COUNTERS:
            first = signed32(counter + delta)
            second = signed32(first + ~delta)
            expected.append({
                "kind": "repeat", "delta": delta, "counterBefore": counter,
                "first": first, "firstFailure": "none", "firstCounter": first,
                "firstSink": first, "second": second, "secondFailure": "none",
                "secondCounter": second, "secondSink": second,
                "tagAfter": "repeat", "tagSame": True,
                "sinkNeighborAfter": 307, "sinkReferenceSame": True,
            })
    for delta in DELTAS:
        for sink_null in (False, True):
            expected.append({
                "kind": "null-receiver", "delta": delta, "sinkNull": sink_null,
                "value": None, "failure": NULL_REFERENCE,
                "sinkLastAfter": None if sink_null else -101,
                "sinkNeighborAfter": None if sink_null else 307,
                "sinkReferenceSame": None if sink_null else True,
            })
    for delta in DELTAS:
        expected.append({
            "kind": "object-instantiation", "delta": delta,
            "counterBefore": (1 << 31) - 1,
            "value": signed32((1 << 31) - 1 + delta), "failure": "none",
            "counterAfter": signed32((1 << 31) - 1 + delta), "tagSame": True,
        })
    return expected


def verify(path, stage, version):
    return verify_report(path, stage, version, "closed-generic-guarded-call", observations(), 4,
                         "Four authored bodies; closed string/object generic instantiations, signed overflow, "
                         "receiver and sink null failures, retained Add effect before sink failure, repeated calls, "
                         "and Tag/sink-reference identity")
