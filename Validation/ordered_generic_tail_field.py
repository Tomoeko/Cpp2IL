"""Independent state expectations for an early Int32 field and later generic value storage."""

from behavior_oracle import verify_report


VALUES = (-(1 << 31), -(1 << 31) + 1, -65536, -32769, -32768, -129, -128, -1,
          0, 1, 2, 127, 128, 255, 256, 32767, 32768, 65535, 65536,
          (1 << 31) - 2, (1 << 31) - 1)
TAIL_VALUES = (-(1 << 63), -(1 << 63) + 1, -(1 << 32), -1, 0, 1,
               (1 << 31) - 1, 1 << 32, (1 << 63) - 1)


def _guard(item):
    bits = item % (1 << 64)
    word = (bits % (1 << 32)) ^ (bits // (1 << 32))
    return (word + (1 << 31)) % (1 << 32) - (1 << 31)


def observations():
    expected = [{"kind": "constructor", "value": value, "valueAfter": value,
                 "tailItem": 0, "tailGuard": 0, "neighbor": 0, "referenceNull": True}
                for value in VALUES]
    for value in VALUES:
        for item in TAIL_VALUES:
            guard = _guard(item)
            for neighbor in (-137, 0, 911):
                for reference_null in (False, True):
                    expected.append({
                        "kind": "read", "value": value, "tailItem": item, "tailGuard": guard,
                        "neighbor": neighbor, "referenceNull": reference_null,
                        "first": value, "second": value, "failure": "none",
                        "valueAfter": value, "tailItemAfter": item, "tailGuardAfter": guard,
                        "neighborAfter": neighbor, "referenceSame": True, "aliasSame": True,
                    })
    expected.append({"kind": "null", "value": None, "failure": "System.NullReferenceException"})
    return expected


def verify(path, stage, version):
    return verify_report(path, stage, version, "ordered-generic-tail-field", observations(), 2,
                         "An Int32 constructor and getter with a later closed generic value field; "
                         "21 signed input boundaries, nine full64 tail patterns, unchanged tail guards/neighbor/reference, "
                         "repeated alias reads and null failure")
