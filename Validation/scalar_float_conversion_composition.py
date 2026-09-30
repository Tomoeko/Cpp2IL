"""Independent two-rounding oracle for binary64 narrowing and binary32 division."""

from behavior_oracle import verify_report
from scalar_float_conversion import convert


SAMPLES = (
    (0x0000000000000000, 0x3F800000), (0x8000000000000000, 0x3F800000),
    (0x3FF0000000000000, 0x40400000), (0xBFF0000000000000, 0x40400000),
    (0x3FF0000000000000, 0xC0400000), (0xBFF0000000000000, 0xC0400000),
    (0x3FF0000010000000, 0x40400000), (0x3FF0000010000001, 0x40400000),
    (0x3FF0000030000000, 0x40400000), (0x47EFFFFFF0000000, 0x7F7FFFFF),
    (0x3690000000000000, 0x00000001), (0x3690000000000001, 0x00000001),
    (0x36A0000000000000, 0x40000000), (0x36B8000000000000, 0x40000000),
    (0x3810000000000000, 0x40000000), (0x380FFFFFF0000000, 0x3F800000),
    (0x7FEFFFFFFFFFFFFF, 0x7F7FFFFF), (0x0010000000000000, 0x00800000),
    (0x3FF0000000000000, 0x00000000), (0x3FF0000000000000, 0x80000000),
    (0x0000000000000000, 0x00000000), (0x8000000000000000, 0x80000000),
    (0x7FF0000000000000, 0x7F800000), (0xFFF0000000000000, 0x3F800000),
    (0x3FF0000000000000, 0x7F800000), (0xBFF0000000000000, 0x7F800000),
    (0x7FF8000000000001, 0x3F800000), (0xFFF8000000000002, 0x3F800000),
    (0x7FF0000000000001, 0x3F800000), (0xFFF0123456789ABC, 0x3F800000),
    (0x3FF0000000000000, 0x7FC00001), (0x3FF0000000000000, 0xFF800002),
)
METHODS = ("staticDivide", "staticNegative", "instanceDivide", "instanceNegative", "fieldDivide", "fieldNegative", "aggregateNegative")


def is_nan(bits):
    return bits & 0x7F800000 == 0x7F800000 and bits & 0x007FFFFF != 0


def divide(left, right):
    # Intel SDM volume 1, NaN propagation and masked invalid arithmetic tables:
    # SSE arithmetic retains the first NaN operand, quiets it, and returns the
    # negative QNaN indefinite for zero/zero and infinity/infinity.
    if is_nan(left):
        return left | 0x00400000
    if is_nan(right):
        return right | 0x00400000
    sign = (left ^ right) & 0x80000000
    left_abs, right_abs = left & 0x7FFFFFFF, right & 0x7FFFFFFF
    if (left_abs == right_abs == 0 or left_abs == right_abs == 0x7F800000):
        return 0xFFC00000
    if left_abs == 0x7F800000 or right_abs == 0:
        return sign | 0x7F800000
    if left_abs == 0 or right_abs == 0x7F800000:
        return sign
    left_exponent, right_exponent = left_abs >> 23, right_abs >> 23
    numerator = (left_abs & 0x007FFFFF) | (0x00800000 if left_exponent else 0)
    denominator = (right_abs & 0x007FFFFF) | (0x00800000 if right_exponent else 0)
    power = (left_exponent or 1) - (right_exponent or 1)
    ratio_log = numerator.bit_length() - denominator.bit_length()
    below = numerator < denominator << ratio_log if ratio_log >= 0 else numerator << -ratio_log < denominator
    ratio_log -= int(below)
    result_power = max(ratio_log + power, -126) - 23
    shift = power - result_power
    scaled_numerator = numerator << shift if shift >= 0 else numerator
    scaled_denominator = denominator if shift >= 0 else denominator << -shift
    rounded, remainder = divmod(scaled_numerator, scaled_denominator)
    rounded += 2 * remainder > scaled_denominator or 2 * remainder == scaled_denominator and rounded & 1
    if rounded >= 0x01000000:
        rounded >>= 1
        result_power += 1
    result_exponent = result_power + 23 + 127 if rounded >= 0x00800000 else 0
    if result_exponent >= 255:
        return sign | 0x7F800000
    return sign | result_exponent << 23 | rounded & 0x007FFFFF


def negative_to_positive(bits):
    return bits & 0x7FFFFFFF if bits & 0x80000000 and bits & 0x7FFFFFFF and not is_nan(bits) else bits


def observations(stage="player"):
    rows = []
    for value, divisor in SAMPLES:
        divisor_argument = divisor | 0x00400000 if stage == "editor" and is_nan(divisor) else divisor
        quotient = divide(convert(value, 64), divisor_argument)
        for method in METHODS:
            result = negative_to_positive(quotient) if method.endswith("Negative") else quotient
            rows.append({"method": method, "inputBits": format(value, "016x"),
                         "divisorBits": format(divisor, "08x"), "argumentBits": format(value, "016x"),
                         "divisorArgumentBits": format(divisor_argument, "08x"), "resultBits": format(result, "08x")})
    rows.extend({"method": method, "exception": "NullReferenceException"} for method in METHODS[2:])
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "scalar-float-conversion-composition", observations(stage), 8,
                         "binary64-to-binary32 conversion before binary32 division, both rounding boundaries, "
                         "subnormal/overflow, signed zero, NaN payloads and ordered negative-only sign selection; "
                         "static/instance/field inputs, unused aggregate ABI slot and null receivers; default rounding and masked exceptions; "
                         "editor binary32 argument quieting asserted; no altered floating-control/status or upper-lane claim")
