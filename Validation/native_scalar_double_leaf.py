"""Integer binary64 oracle for ordered field addition and signed Int64 scaling."""

from behavior_oracle import verify_report
from scalar_double_accumulator import EXPONENT, FRACTION, SIGN, add_bits, finite_units, hex_bits, rounded_units


SUM_SAMPLES = (
    (0, 0), (SIGN, 0), (0, SIGN), (SIGN, SIGN),
    (0x3ff0000000000000, 0x4000000000000000),
    (0x3ff0000000000000, 0xbff0000000000000),
    (1, 1), (SIGN | 1, 1), (0x000fffffffffffff, 1),
    (0x0010000000000000, SIGN | 1),
    (0x4340000000000000, 0x3ff0000000000000),
    (0x4340000000000001, 0x3ff0000000000000),
    (0x7fefffffffffffff, 0x7fefffffffffffff),
    (0xffefffffffffffff, 0xffefffffffffffff),
    (EXPONENT, 0x3ff0000000000000),
    (SIGN | EXPONENT, 0xbff0000000000000),
    (EXPONENT, SIGN | EXPONENT),
    (0x7ff8000000000042, 0x3ff0000000000000),
    (0x3ff0000000000000, 0xfff8000000000002),
)
LONG_SAMPLES = (
    0, 1, -1, 3, -3,
    (1 << 53) - 1, 1 << 53, (1 << 53) + 1, (1 << 53) + 2, (1 << 53) + 3,
    -(1 << 53) + 1, -(1 << 53), -(1 << 53) - 1, -(1 << 53) - 2, -(1 << 53) - 3,
    (1 << 54) - 1, (1 << 54) + 1, (1 << 54) + 3,
    (1 << 63) - 1, (1 << 63) - 2, -(1 << 63), -(1 << 63) + 1,
)
COEFFICIENTS = (('Eighth', 0x3fc0000000000000), ('EighthAlias', 0x3fc0000000000000),
                ('Tenth', 0x3fb999999999999a))


def rounded_ratio_units(numerator, denominator):
    """Round a rational multiple of 2^-1074 without using host floating point."""
    if not numerator:
        return 0
    sign = SIGN if numerator < 0 else 0
    magnitude = abs(numerator)
    exponent = magnitude.bit_length() - denominator.bit_length()
    if exponent >= 0:
        exponent -= magnitude < denominator << exponent
    else:
        exponent -= magnitude << -exponent < denominator
    shift = max(exponent - 52, 0)
    quantum = denominator << shift
    significand, remainder = divmod(magnitude, quantum)
    if remainder * 2 > quantum or remainder * 2 == quantum and significand & 1:
        significand += 1
    if significand == 1 << 53:
        significand >>= 1
        shift += 1
    if significand < 1 << 52:
        return sign | significand
    exponent = shift + 1
    if exponent >= 0x7ff:
        return sign | EXPONENT
    return sign | exponent << 52 | significand & FRACTION


def scaled_long(value, coefficient):
    # Conversion rounds before multiplication; folding the rational expression
    # would lose the native CVTSI2SD rounding at the mantissa boundary.
    converted = rounded_units(value << 1074)
    return rounded_ratio_units(finite_units(converted) * finite_units(coefficient), 1 << 1074)


def observations():
    rows = [
        {'kind': 'declarations', 'methods': 7, 'fields': 4, 'types': 2,
         'sumReturn': 'System.Double', 'sumParameters': 0, 'firstType': 'System.Double',
         'secondType': 'System.Double', 'conversionParameter': 'System.Int64',
         'conversionReturn': 'System.Double', 'conversionStatic': True,
         'markersReadonly': True, 'typeInitializers': 2},
        {'kind': 'defaults', 'firstBits': hex_bits(0), 'secondBits': hex_bits(0),
         'fieldsMarker': 17, 'conversionsMarker': 29},
    ]
    for sample, (first, second) in enumerate(SUM_SAMPLES):
        for kind in ('sum', 'repeat-alias'):
            rows.append({'kind': kind, 'sample': sample, 'alias': kind == 'repeat-alias',
                         'exception': 'none', 'firstBits': hex_bits(first), 'secondBits': hex_bits(second),
                         'resultBits': hex_bits(add_bits(first, second))})
    rows.append({'kind': 'null-owner', 'exception': 'System.NullReferenceException'})
    for value in LONG_SAMPLES:
        for method, coefficient in COEFFICIENTS:
            rows.append({'kind': 'conversion', 'method': method, 'input': str(value),
                         'exception': 'none', 'resultBits': hex_bits(scaled_long(value, coefficient)),
                         'fieldsMarker': 17, 'conversionsMarker': 29})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, 'native-scalar-double-leaf', observations(), 7,
                         'full seven-method assembly; ordered binary64 field addition, input and alias preservation, '
                         'null owner, signed Int64 conversion followed by separately rounded multiplication, '
                         'mantissa boundaries and type initializers; default rounding and masked exceptions')
