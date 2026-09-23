"""Candidate Windows x64 scalar truncation oracle; exact original evidence is required."""

import json
import math
import struct


# Exact nonnegative binary64 encodings, tested with both signs. No decimal-to-double
# conversion participates in fixture input selection or report comparison.
MAGNITUDES = (
    "0000000000000000", "0000000000000001", "000fffffffffffff", "0010000000000000",
    "3fdfffffffffffff", "3fe0000000000000", "3fefffffffffffff", "3ff0000000000000",
    "3ff0000000000001", "3ff8000000000000", "3fffffffffffffff", "4004000000000000",
    "41dfffffffa00000", "41dfffffffc00000", "41dfffffffe00000", "41dfffffffffffff",
    "41e0000000000000", "41e0000000000001", "41e0000000100000", "41e0000000200000",
    "41e0000000200001", "43dffffffffffffe", "43dfffffffffffff", "43e0000000000000",
    "43e0000000000001", "43e0000000000002", "433fffffffffffff", "4340000000000000",
    "4340000000000001", "7fefffffffffffff", "7ff0000000000000", "7ff8000000000001",
    "7ffb123456789abc",
)


def truncated_signed_bits(input_bits, width):
    """Round toward zero, then range-check as integers; masked invalid yields MIN."""
    if width not in (32, 64):
        raise ValueError("Scalar truncation requires a signed 32-bit or 64-bit result")
    value = struct.unpack(">d", bytes.fromhex(input_bits))[0]
    minimum = -(1 << (width - 1))
    maximum = (1 << (width - 1)) - 1
    result = math.trunc(value) if math.isfinite(value) else minimum
    if not minimum <= result <= maximum:
        result = minimum
    return format(result % (1 << width), "0" + str(width // 4) + "x")


def observations():
    expected = []
    for magnitude in MAGNITUDES:
        for sign in (0, 1):
            input_bits = format(int(magnitude, 16) | (sign << 63), "016x")
            expected.append({"inputBits": input_bits,
                             "int32Bits": truncated_signed_bits(input_bits, 32),
                             "int64Bits": truncated_signed_bits(input_bits, 64)})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "scalar-truncation"):
        raise ValueError("Scalar truncation report has the wrong version, stage or profile")
    platforms = {"editor": "WindowsEditor", "player": "WindowsPlayer"}
    if stage not in platforms or report.get("platform") != platforms[stage]:
        raise ValueError("Scalar truncation observations require the exact Windows editor or player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Scalar truncation behavior differs from the candidate Windows x64 bit-pattern oracle")
    return {"status": "passed", "observations": len(expected), "resultChecks": 2 * len(expected),
            "methods": 2, "platform": report["platform"], "profile": "scalar-truncation",
            "scope": "66 binary64 input patterns and masked-invalid output bits; requires both original exact-target stages before recovery acceptance"}
