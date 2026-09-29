"""Independent expectations for closed generic value storage preceding a scalar access."""

from behavior_oracle import verify_report

VALUES = (-(1 << 31), -(1 << 31) + 1, -65536, -32769, -32768, -129, -128, -1,
          0, 1, 2, 127, 128, 255, 256, 32767, 32768, 65535, 65536,
          (1 << 31) - 2, (1 << 31) - 1)
INT_ITEMS = (-(1 << 31), -65536, -1, 0, 1, 65535, (1 << 31) - 1)
LONG_ITEMS = (-(1 << 63), -(1 << 63) + 1, -(1 << 32), -1, 0, 1,
              (1 << 31) - 1, 1 << 32, (1 << 63) - 1)


def _stamp(item):
    bits = item % (1 << 64)
    folded = (bits % (1 << 32)) ^ (bits // (1 << 32))
    return (folded + (1 << 31)) % (1 << 32) - (1 << 31)


def observations():
    expected = []
    for value in VALUES:
        for carrier in ("i4", "i8"):
            expected.append({"kind": "constructor", "carrier": carrier, "value": value,
                             "valueAfter": value, "item": 0, "stamp": 0,
                             "neighbor": 0, "referenceNull": True})
    for carrier, items in (("i4", INT_ITEMS), ("i8", LONG_ITEMS)):
        for value in VALUES:
            for item in items:
                stamp = _stamp(item)
                for neighbor in (-137, 0, 911):
                    for reference_null in (False, True):
                        expected.append({"kind": "state", "carrier": carrier, "value": value,
                                         "item": item, "stamp": stamp, "neighbor": neighbor,
                                         "referenceNull": reference_null, "first": value, "repeat": value,
                                         "clearedRead": 0, "failure": "none", "valueAfter": 0,
                                         "itemAfter": item, "stampAfter": stamp, "neighborAfter": neighbor,
                                         "referenceSame": True, "aliasSame": True})
    for carrier in ("i4", "i8"):
        for operation in ("read", "clear"):
            expected.append({"kind": "null", "carrier": carrier, "operation": operation,
                             "value": None, "failure": "System.NullReferenceException"})
    return expected


def verify(path, stage, version):
    return verify_report(path, stage, version, "closed-generic-storage", observations(), 6,
                         "Closed Tail<Int32>/Tail<Int64> precedes an accessed Int32; constructor defaults, "
                         "21 scalar boundaries, full32/full64 prefix patterns, repeated alias reads, clear then read, "
                         "unchanged prefix/stamp/neighbor/reference and null read/store failures")
