"""Independent observations for fields preceding an open generic parameter."""

from behavior_oracle import verify_report


VALUES = (-(1 << 31), -65536, -129, -1, 0, 1, 127, 65535, (1 << 31) - 1)
PAYLOADS = (("int", str(-(1 << 31))), ("long", str((1 << 63) - 1)),
            ("string", "open"))


def observations():
    expected = []
    for kind, payload in PAYLOADS:
        expected.append({"kind": "constructor", "type": kind, "first": 0,
                         "anchorNull": True, "flag": False, "payloadDefault": True})
        for first in VALUES:
            for has_anchor in (False, True):
                expected.append({
                    "kind": "read", "type": kind, "first": first,
                    "anchorPresent": has_anchor, "payload": payload,
                    "firstResult": first, "anchorSame": True,
                    "flag": (first & 1) != 0, "flagResult": (first & 1) != 0,
                    "firstAfter": first, "anchorAfterSame": True,
                    "flagAfter": (first & 1) != 0,
                    "payloadAfter": payload, "aliasSame": True, "failure": "none",
                })
    for getter in ("first", "anchor", "flag"):
        expected.append({"kind": "null", "getter": getter,
                         "failure": "System.NullReferenceException"})
    return expected


def verify(path, stage, version):
    return verify_report(path, stage, version, "open-generic-early-field",
                         observations(), 4,
                         "Three open generic instance getters and their implicit constructor; "
                         "three payload storage kinds, early Int32/reference fields, aliases, "
                         "unchanged later payloads and null receiver failures")
