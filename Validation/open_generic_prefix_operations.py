"""Independent observations for arithmetic on fixed fields preceding open T storage."""

from behavior_oracle import verify_report


VALUES = (-(1 << 31), -17, -1, 0, 1, 17, (1 << 31) - 1)
DELTAS = (-1, 0, 1)
PAYLOADS = (("int", str(-(1 << 31))), ("long", str((1 << 63) - 1)),
            ("string", "open"))


def int32(value):
    return ((value + (1 << 31)) % (1 << 32)) - (1 << 31)


def observations():
    expected = []
    for kind, payload in PAYLOADS:
        expected.append({"kind": "constructor", "type": kind, "first": 0,
                         "second": 0, "anchorNull": True, "payloadDefault": True})
        for first in VALUES:
            for delta in DELTAS:
                second = ~first
                updated = int32(first + delta)
                expected.append({
                    "kind": "update", "type": kind, "first": first,
                    "second": second, "delta": delta,
                    "result": int32(updated + second),
                    "firstAfter": updated, "secondAfter": second,
                    "anchorSame": True, "payloadAfter": payload,
                    "aliasSame": True, "failure": "none",
                })
    expected.append({"kind": "null", "failure": "System.NullReferenceException"})
    return expected


def verify(path, stage, version):
    return verify_report(path, stage, version, "open-generic-prefix-operations",
                         observations(), 2,
                         "Open generic fixed-prefix arithmetic and write order across three later T storage kinds")
