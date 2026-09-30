"""IEEE width conversion oracle using integer significands and ties-to-even rounding."""

from behavior_oracle import verify_report
from scalar_float_selection import SELECTION_BITS


INPUT_BITS = {
    32: SELECTION_BITS[32] + (0x007FFFFF, 0x3F000000),
    64: SELECTION_BITS[64] + (
        0x3FF0000010000000, 0x3FF0000010000001, 0x3FF0000030000000, 0xBFF0000010000000,
        0x47EFFFFFE0000000, 0x47EFFFFFF0000000, 0x47EFFFFFEFFFFFFF,
        0x3810000000000000, 0x380FFFFFC0000000, 0x3690000000000000,
        0x3690000000000001, 0x380FFFFFF0000000, 0x7FF8123456789ABC, 0xFFF0123456789ABC),
}


def convert(bits, source_width):
    source_fraction, source_exponent = (23, 8) if source_width == 32 else (52, 11)
    result_fraction, result_exponent = (52, 11) if source_width == 32 else (23, 8)
    result_width = 64 if source_width == 32 else 32
    sign = (bits >> (source_width - 1)) << (result_width - 1)
    fraction = bits & ((1 << source_fraction) - 1)
    exponent = (bits >> source_fraction) & ((1 << source_exponent) - 1)
    maximum_exponent = (1 << result_exponent) - 1
    if exponent == (1 << source_exponent) - 1:
        payload = fraction << (result_fraction - source_fraction) if result_fraction > source_fraction else fraction >> (source_fraction - result_fraction)
        if fraction:
            payload |= 1 << (result_fraction - 1)
        return sign | (maximum_exponent << result_fraction) | payload
    source_bias, result_bias = (1 << (source_exponent - 1)) - 1, (1 << (result_exponent - 1)) - 1
    significand = fraction | ((1 << source_fraction) if exponent else 0)
    if significand == 0:
        return sign
    source_power = (exponent - source_bias if exponent else 1 - source_bias) - source_fraction
    result_power = max(significand.bit_length() - 1 + source_power, 1 - result_bias) - result_fraction
    shift = source_power - result_power
    if shift >= 0:
        rounded = significand << shift
    else:
        rounded, remainder = divmod(significand, 1 << -shift)
        half = 1 << (-shift - 1)
        rounded += remainder > half or remainder == half and rounded & 1
    if rounded >= 1 << (result_fraction + 1):
        rounded >>= 1
        result_power += 1
    result_field = result_power + result_fraction + result_bias if rounded >= 1 << result_fraction else 0
    if result_field >= maximum_exponent:
        return sign | (maximum_exponent << result_fraction)
    return sign | (result_field << result_fraction) | (rounded & ((1 << result_fraction) - 1))


def observations(stage="player"):
    rows = []
    for width, samples in INPUT_BITS.items():
        for bits in samples:
            argument = bits
            if stage == "editor" and width == 32 and bits & 0x7F800000 == 0x7F800000 and bits & 0x007FFFFF:
                argument |= 0x00400000
            rows.append({"sourceWidth": width, "inputBits": format(bits, "0%dx" % (width // 4)),
                         "argumentBits": format(argument, "0%dx" % (width // 4)),
                         "resultBits": format(convert(argument, width), "016x" if width == 32 else "08x")})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "scalar-float-conversion", observations(stage), 2,
                         "scalar parameter/return conversion bits, ties to even, overflow, subnormal boundaries, "
                         "signed zeros and NaN payload conversion; default rounding, masked exceptions; "
                         "editor binary32 argument quieting asserted; no floating-control/status or upper-lane claim")
