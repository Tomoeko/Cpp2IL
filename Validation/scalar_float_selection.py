"""Independent scalar selection result-bit oracle with both zero signs and NaN payloads."""

import struct

from behavior_oracle import verify_report
from float_comparison import BITS


SELECTION_BITS = {width: bits + ((0x7F800001, 0xFF800002) if width == 32 else
                                (0x7FF0000000000001, 0xFFF0000000000002))
                  for width, bits in BITS.items()}


def _number(bits, width):
    return struct.unpack(">f" if width == 32 else ">d", bits.to_bytes(width // 8, "big"))[0]


def _bits(bits, width):
    return format(bits, "0%dx" % (width // 4))


def _argument_bits(bits, width, stage):
    # The supplied editor's binary32 managed argument conversion quiets signaling
    # NaNs. The native player preserves them. The harness asserts both inputs.
    signaling = (width == 32 and bits & 0x7F800000 == 0x7F800000 and
                 bits & 0x007FFFFF and not bits & 0x00400000)
    return bits | 0x00400000 if stage == "editor" and signaling else bits


def observations(stage="player"):
    rows = []
    for width, bits in SELECTION_BITS.items():
        for left_bits in bits:
            for right_bits in bits:
                left_argument, right_argument = _argument_bits(left_bits, width, stage), _argument_bits(right_bits, width, stage)
                left, right = _number(left_argument, width), _number(right_argument, width)
                rows.append({"kind": "pair", "width": width,
                             "leftBits": _bits(left_bits, width), "rightBits": _bits(right_bits, width),
                             "leftArgumentBits": _bits(left_argument, width), "rightArgumentBits": _bits(right_argument, width),
                             "minimumBits": _bits(left_argument if left < right else right_argument, width),
                             "maximumBits": _bits(left_argument if left > right else right_argument, width)})
    rows.append({"kind": "constructor", "value32": "00000000", "value64": "0000000000000000", "neighbor": 0})
    for width, bits in SELECTION_BITS.items():
        other_width = 64 if width == 32 else 32
        other_bits = int.from_bytes(struct.pack(">d" if other_width == 64 else ">f", -17), "big")
        for input_bits in bits:
            result = input_bits if _number(input_bits, width) > 0 else 0
            rows.append({"kind": "field", "width": width, "inputBits": _bits(input_bits, width),
                         "readBits": _bits(result, width), "storedBits": _bits(result, width),
                         "otherBits": _bits(other_bits, other_width), "neighbor": 31})
    for width in (32, 64):
        for operation in ("read", "store"):
            rows.append({"kind": "null-receiver", "width": width, "operation": operation,
                         "exception": "System.NullReferenceException", "resultBits": None})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "scalar-float-selection", observations(stage), 9,
                         "scalar result bits, signed zeros, infinities, subnormals and NaN payloads; "
                         "native player signaling-NaN bits; editor binary32 argument conversion asserted; "
                         "managed null-receiver read/store calls; "
                         "masked exceptions, no altered MXCSR or upper-lane claim")
