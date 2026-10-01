"""Oracle for complete 12-byte by-value argument copies and Single arithmetic."""

import math
import struct
from behavior_oracle import verify_report


TYPE = "NativeStackAggregateArgumentFixture.Triple"
CASES = ((0, 0, 0), (0x80000000, 0x80000000, 0x80000000), (0x80000000, 0, 0x80000000),
         (0x3f800000, 0x40000000, 0x40400000), (0x4b800000, 0x3f800000, 0xcb800000),
         (0x7f7fffff, 0xff7fffff, 1), (0x7f7fffff, 0x7f7fffff, 0xff7fffff),
         (1, 2, 3), (0x80000001, 0x80000002, 0x80000003),
         (0x7f800000, 0x3f800000, 0xc0000000), (0xff800000, 0x3f800000, 0xc0000000),
         (0x7fc12345, 0x3f800000, 0x40000000), (0x3f800000, 0x40000000, 0x7fc23456))


def single(value):
    try:
        return struct.unpack("<f", struct.pack("<f", value))[0]
    except OverflowError:
        return math.copysign(math.inf, value)


def observations(stage="player"):
    if stage not in ("editor", "player"):
        raise ValueError("The aggregate oracle requires a known editor or player stage")
    rows = [{"kind": "declarations", "types": 2, "methods": 2,
             "fields": [{"name": name, "type": "System.Single", "offset": offset}
                        for name, offset in (("First", 0), ("Second", 4), ("Third", 8))],
             "size": 12, "sequential": True, "sumArgument": TYPE, "forwardArgument": TYPE,
             "return": "System.Single"}]
    for index, case in enumerate(CASES):
        first, second, third = [struct.unpack("<f", struct.pack("<I", bits))[0] for bits in case]
        # The supplied Windows Mono editor keeps a wider intermediate sum.
        # The exact Release IL2CPP target rounds each native Single addition.
        # Model these independently; an editor pass cannot prove native behavior.
        result = single(first + second + third) if stage == "editor" else single(single(first + second) + third)
        for operation in range(2):
            value = result if operation == 0 else single(result + 1.0)
            bits = struct.unpack("<I", struct.pack("<f", value))[0]
            rows.append({"kind": "aggregate", "case": index, "operation": operation,
                         "first": format(case[0], "08x"), "second": format(case[1], "08x"),
                         "third": format(case[2], "08x"), "result": format(bits, "08x"), "exception": "none"})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-stack-aggregate-argument", observations(stage), 2,
                         "full two-method assembly; exact 12-byte by-value arguments, all three field lanes, "
                         "stage-specific intermediate precision, cancellation, subnormal values, infinities and NaN payloads")
