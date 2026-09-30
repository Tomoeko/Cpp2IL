"""Integer binary64 oracle for a separately rounded field subtraction and addition.

Multiple quiet-NaN input rows have classification-only result expectations here.
Their payloads remain raw observations and require an authenticated original/rebuilt
comparison; this oracle does not infer the compiler's native operand order.
"""

import re

from behavior_oracle import read_report, verify_report


PROFILE = "scalar-double-accumulator"
SIGN = 1 << 63
FRACTION = (1 << 52) - 1
EXPONENT = 0x7ff0000000000000
QUIET = 1 << 51
INVALID_NAN = 0xfff8000000000000
CLASSIFICATION_ONLY_SAMPLES = frozenset((6, 7))
SAMPLES = (
    (0x0000000000000000, 0x0000000000000000, 0x0000000000000000),
    (0x8000000000000000, 0x0000000000000000, 0x8000000000000000),
    (0x0000000000000000, 0x8000000000000000, 0x8000000000000000),
    (0x8000000000000000, 0x8000000000000000, 0x8000000000000000),
    (0x3ff0000000000000, 0x0000000000000000, 0x4000000000000000),
    (0xbff0000000000000, 0x3ff0000000000000, 0x4000000000000000),
    (0x7ff8000000000042, 0xfff8000000000002, 0x0000000000000000),
    (0x7ff8000000000042, 0x0000000000000000, 0x7ff8123456789abc),
    (0x0000000000000001, 0x0000000000000000, 0x0000000000000000),
    (0x8000000000000001, 0x0000000000000000, 0x8000000000000000),
    (0x000fffffffffffff, 0x8000000000000001, 0x0000000000000000),
    (0x0010000000000000, 0x0000000000000001, 0x0000000000000000),
    (0x0010000000000000, 0x000fffffffffffff, 0x0000000000000000),
    (0x000fffffffffffff, 0x000fffffffffffff, 0x8000000000000001),
    (0x0000000000000001, 0x8000000000000001, 0x8000000000000001),
    (0x8010000000000000, 0x800fffffffffffff, 0x0000000000000000),
    (0x0010000000000000, 0x0000000000000000, 0x800fffffffffffff),
    (0x4340000000000000, 0xbff0000000000000, 0xc340000000000000),
    (0x4340000000000000, 0xc008000000000000, 0xc340000000000000),
    (0x4340000000000000, 0x3ff0000000000000, 0xc340000000000000),
    (0x3ff0000000000000, 0xbca0000000000000, 0xbff0000000000000),
    (0x3ff0000000000001, 0xbca0000000000000, 0xbff0000000000000),
    (0x3ff0000000000000, 0x0000000000000000, 0x3ca0000000000000),
    (0x3ff0000000000001, 0x0000000000000000, 0x3ca0000000000000),
    (0x7fefffffffffffff, 0xffefffffffffffff, 0xffefffffffffffff),
    (0x7ff0000000000000, 0x3ff0000000000000, 0xfff0000000000000),
    (0x7ff0000000000000, 0x7ff0000000000000, 0x0000000000000000),
    (0xfff0000000000000, 0x3ff0000000000000, 0x3ff0000000000000),
    (0x3ff0000000000000, 0xfff0000000000000, 0x3ff0000000000000),
    (0x7ff8000000000042, 0x3ff0000000000000, 0x0000000000000000),
    (0x3ff0000000000000, 0xfff8000000000002, 0x0000000000000000),
    (0x3ff0000000000000, 0x0000000000000000, 0x7ff8123456789abc),
)


def is_nan(bits):
    return bits & EXPONENT == EXPONENT and bool(bits & FRACTION)


def finite_units(bits):
    exponent = (bits >> 52) & 0x7ff
    magnitude = bits & FRACTION
    if exponent:
        magnitude = ((1 << 52) | magnitude) << (exponent - 1)
    return -magnitude if bits & SIGN else magnitude


def rounded_units(value, zero_sign=0):
    """Round exact integer multiples of 2^-1074 to nearest, ties to even."""
    if not value:
        return zero_sign
    sign = SIGN if value < 0 else 0
    magnitude = abs(value)
    shift = max(magnitude.bit_length() - 53, 0)
    significand = magnitude >> shift
    if shift:
        remainder = magnitude - (significand << shift)
        halfway = 1 << (shift - 1)
        if remainder > halfway or remainder == halfway and significand & 1:
            significand += 1
    if significand == 1 << 53:
        significand >>= 1
        shift += 1
    if significand < 1 << 52:
        return sign | significand
    exponent = shift + 1
    if exponent >= 0x7ff:
        return sign | EXPONENT
    return sign | (exponent << 52) | (significand & FRACTION)


def add_bits(left, right, subtract=False):
    """Model one binary64 ADD/SUB with masked invalid and default rounding."""
    # The matrix's exact NaN rows contain one NaN; native priority is undisclosed
    # for its two multiple-NaN rows and is not accepted as an exact expectation.
    if is_nan(left):
        return left | QUIET
    if is_nan(right):
        return right | QUIET
    effective_right = right ^ (SIGN if subtract else 0)
    left_infinite = left & ~SIGN == EXPONENT
    right_infinite = effective_right & ~SIGN == EXPONENT
    if left_infinite or right_infinite:
        if left_infinite and right_infinite and (left ^ effective_right) & SIGN:
            return INVALID_NAN
        return left if left_infinite else effective_right
    value = finite_units(left) + finite_units(effective_right)
    # Under round-to-nearest, opposing signed zero or exact cancellation is +0.
    same_negative_zero = left == SIGN and effective_right == SIGN
    return rounded_units(value, SIGN if same_negative_zero else 0)


def applied_total(current, baseline, total):
    return add_bits(total, add_bits(current, baseline, subtract=True))


def hex_bits(bits):
    return format(bits, "016x")


def state(current=0, baseline=0, total=0):
    return {"pending": False, "currentBits": hex_bits(current),
            "baselineBits": hex_bits(baseline), "totalBits": hex_bits(total)}


def observations():
    rows = [
        {"kind": "declarations", "methods": 2, "fields": 4, "pendingType": "System.Boolean",
         "currentType": "System.Double", "baselineType": "System.Double", "totalType": "System.Double",
         "applyParameters": 0, "applyReturn": "System.Void"},
        {"kind": "defaults", "state": state()},
    ]
    for sample, (current, baseline, total) in enumerate(SAMPLES):
        result = applied_total(current, baseline, total)
        for kind in ("disabled", "enabled", "repeat-alias"):
            rows.append({"kind": kind, "sample": sample, "alias": kind == "repeat-alias", "exception": "none",
                         "currentInput": hex_bits(current), "baselineInput": hex_bits(baseline),
                         "totalInput": hex_bits(total),
                         "state": state(current, baseline, total if kind == "disabled" else result)})
    rows.append({"kind": "null-owner", "exception": "System.NullReferenceException", "state": None})
    return rows


def verify(path, stage, version):
    report = read_report(path)
    expected = observations()
    actual = report.get("observations") if isinstance(report, dict) else None
    classified = []
    if isinstance(actual, list) and len(actual) == len(expected):
        for index, row in enumerate(expected):
            if row.get("sample") not in CLASSIFICATION_ONLY_SAMPLES or row["kind"] == "disabled":
                continue
            actual_row = actual[index]
            bits = actual_row.get("state", {}).get("totalBits") if isinstance(actual_row, dict) and \
                isinstance(actual_row.get("state"), dict) else None
            if not isinstance(bits, str) or not re.fullmatch(r"[0-9a-f]{16}", bits) or \
                    not is_nan(int(bits, 16)) or not int(bits, 16) & QUIET:
                raise ValueError(PROFILE + " multiple-NaN result is not a raw quiet-NaN bit pattern")
            if row["kind"] == "repeat-alias":
                previous = actual[index - 1]
                if not isinstance(previous, dict) or not isinstance(previous.get("state"), dict) or \
                        previous["state"].get("totalBits") != bits:
                    raise ValueError(PROFILE + " disabled repeat changed its captured quiet-NaN bits")
            row["state"]["totalBits"] = bits
            classified.append({"sample": row["sample"], "kind": row["kind"]})
    result = verify_report(path, stage, version, PROFILE, expected, 2,
                           "separately rounded binary64 subtraction/addition under nearest-even and masked "
                           "exceptions; exact finite/single-NaN/invalid results and no-op input preservation; "
                           "four active multiple-quiet-NaN results classified only, raw payload equality "
                           "requires authenticated original/rebuilt observation comparison")
    result["classificationOnlyResults"] = classified
    result["completeRawResultOracle"] = False
    result["unverifiedOracleFacts"] = ["Native operand priority for multiple quiet-NaN payloads"]
    return result
