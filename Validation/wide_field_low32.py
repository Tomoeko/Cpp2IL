"""Independent low-word arithmetic for signed and unsigned 64-bit fields."""

from behavior_oracle import verify_report


LOW_WORDS = (0, 1, 2, 0x7f, 0x80, 0x7fff, 0x8000, 0x7ffffffe, 0x7fffffff,
             0x80000000, 0xfffffffe, 0xffffffff)
HIGH_WORDS = (0, 1, 0x7fffffff, 0x80000000, 0xffffffff, 0x12345678, 0x80000001, 0xffff0000)
ROUTES = (("signed", "signed"), ("signed", "unsigned"), ("unsigned", "signed"), ("unsigned", "unsigned"))


def observations():
    expected = [{"kind": "constructor", "owner": owner, "bits": 0, "neighbor": 0, "referenceNull": True}
                for owner in ("signed", "unsigned")]
    for high in HIGH_WORDS:
        for low in LOW_WORDS:
            bits = (high << 32) | low
            for neighbor in (-(1 << 31), 0, (1 << 31) - 1):
                for has_reference in (False, True):
                    for owner, route in ROUTES:
                        result = low if route == "unsigned" else (low + (1 << 31)) % (1 << 32) - (1 << 31)
                        expected.append({
                            "kind": "read", "owner": owner, "route": route,
                            "bitsBefore": bits, "neighborBefore": neighbor,
                            "referenceNullBefore": not has_reference, "result": result, "failure": "none",
                            "bitsAfter": bits, "neighborAfter": neighbor, "referenceSame": True,
                        })
    expected.extend({"kind": "null", "owner": owner, "route": route,
                     "result": None, "failure": "System.NullReferenceException"} for owner, route in ROUTES)
    return expected


def verify(path, stage, version):
    return verify_report(path, stage, version, "wide-field-low32", observations(), 6,
                         "Four signed/unsigned low32 getters and two constructors; 96 complete64 patterns, "
                         "unchanged field/integer neighbor/reference identity and four null receiver failures")
