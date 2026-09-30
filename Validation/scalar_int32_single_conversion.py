"""Integer-only signed Int32 conversion, separately rounded ratio and enum oracle."""

from behavior_oracle import verify_report
from scalar_float_conversion_composition import divide


SAMPLES = (
    0, 1, -1, 3, -3,
    16777215, 16777216, 16777217, 16777218, 16777219,
    -16777215, -16777216, -16777217, -16777218, -16777219,
    33554431, 33554432, 33554433, 33554434, 33554435,
    -33554431, -33554432, -33554433, -33554434, -33554435,
    1073741823, 1073741825, -1073741825,
    2147483647, 2147483646, -2147483648, -2147483647,
)
RATIOS = (
    (0, 1), (0, -1), (0, 0), (1, 0), (-1, 0),
    (1, 3), (-1, 3), (1, -3), (-1, -3),
    (-2147483648, 2147483647), (2147483647, -2147483648),
    (2147483647, 1), (-2147483648, -1),
    (16777217, 16777216), (16777219, 16777217), (16777217, 16777219),
    (-16777219, 16777217), (33554435, 33554433), (1073741825, 1073741823),
    (2147483647, 2147483647), (-2147483648, -2147483648),
    (-2147483648, 1), (2147483647, -1), (3, 16777219),
)
METHODS = ("StoreFirst", "StoreSecond", "Convert", "ConvertStatic")
NULL_METHODS = ("StoreFirst", "StoreSecond", "Convert", "Ratio", "ScaledChoice")


def single_bits(value):
    if type(value) is not int or not -2147483648 <= value <= 2147483647:
        raise ValueError("A signed Int32 input is required")
    if value == 0:
        return 0
    sign = 0x80000000 if value < 0 else 0
    magnitude = abs(value)
    exponent = magnitude.bit_length() - 1
    shift = exponent - 23
    if shift <= 0:
        rounded = magnitude << -shift
    else:
        rounded, remainder = divmod(magnitude, 1 << shift)
        half = 1 << (shift - 1)
        rounded += remainder > half or remainder == half and rounded & 1
    if rounded >= 1 << 24:
        rounded >>= 1
        exponent += 1
    return sign | (exponent + 127) << 23 | rounded & 0x007FFFFF


def scaled_choice(value):
    decremented = (value - 1 + 2**31) % 2**32 - 2**31
    bits = single_bits(decremented)
    # Every nonzero converted integer is a normal Single of magnitude >= 1.
    # Multiplication by one half is exact and reduces only its exponent.
    return bits - 0x00800000 if bits & 0x7FFFFFFF else bits


def state(count=43, divisor=-7, choice=29, first=0x80000000, second=0x00000001, arrays=True):
    return {"count": count, "divisor": divisor, "choice": choice,
            "firstBits": format(first, "08x"), "secondBits": format(second, "08x"),
            "samples": [5, -17] if arrays else None,
            "batches": [[5, -17], None, [5, -17]] if arrays else None,
            "firstBatchIsSamples": arrays, "repeatedBatchAlias": arrays}


def operation(method, value, current, kind="operation"):
    after = dict(current)
    result = None
    if method == "StoreFirst":
        after["firstBits"] = format(single_bits(value), "08x")
    elif method == "StoreSecond":
        after["secondBits"] = format(single_bits(value), "08x")
    elif method in ("Convert", "ConvertStatic"):
        result = single_bits(value)
    elif method == "Ratio":
        result = divide(single_bits(current["count"]), single_bits(current["divisor"]))
    elif method == "ScaledChoice":
        result = scaled_choice(current["choice"])
    else:
        raise ValueError("Unknown conversion operation")
    row = {"kind": kind, "method": method, "input": value, "exception": "none",
           "resultBits": None if result is None else format(result, "08x"), **after,
           "sameSamples": True, "sameBatches": True, "sameFirstBatch": True}
    if kind == "reuse":
        row["sameOwner"] = True
    return row, after


def observations():
    rows = [{"kind": "declarations", "methods": 7, "fields": 11,
             "countType": "System.Int32", "divisorType": "System.Int32",
             "choiceType": "ScalarInt32SingleConversionFixture.ConversionChoice",
             "firstType": "System.Single", "secondType": "System.Single",
             "samplesType": "System.Int32[]", "samplesRank": 1, "samplesElement": "System.Int32",
             "batchesType": "System.Int32[][]", "batchesRank": 1, "batchesElement": "System.Int32[]",
             "batchesNestedRank": 1, "batchesNestedElement": "System.Int32",
             "enumUnderlying": "System.Int32", "enumValues": [0, 1, 2],
             "signatures": {method: ["System.Void" if method.startswith("Store") else "System.Single", "System.Int32"]
                            for method in METHODS} | {"Ratio": ["System.Single"], "ScaledChoice": ["System.Single"]},
             "instanceConvertIsStatic": False, "staticConvertIsStatic": True},
            {"kind": "defaults", **state(0, 0, 0, 0, 0, arrays=False)}]
    for value in SAMPLES:
        for method in METHODS:
            rows.append(operation(method, value, state())[0])
    for count, divisor in RATIOS:
        rows.append(operation("Ratio", count, state(count, divisor))[0])
    for value in SAMPLES:
        rows.append(operation("ScaledChoice", value, state(choice=value))[0])
    rows.extend({"kind": "null-owner", "method": method, "exception": "System.NullReferenceException"}
                for method in NULL_METHODS)
    current = state()
    for method, value in (("StoreFirst", 2147483647), ("StoreSecond", -2147483648),
                          ("Convert", 16777219), ("Ratio", 43)):
        row, current = operation(method, value, current, "reuse")
        rows.append(row)
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "scalar-int32-single-conversion", observations(), 7,
                         "signed Int32-to-Single rounding and ABI slots; two own-field stores, separately rounded "
                         "integer-field ratio, enum decrement wrapping before exact half scaling; all marker bits, "
                         "declarations, unused nested-array identities, contents and aliases, null receivers and "
                         "shared-holder reuse; default rounding and masked invalid division")
