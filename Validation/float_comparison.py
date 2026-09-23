"""Independent finite IEEE comparison oracle; no JSON floating-point values."""

import json
import struct


BITS = {
    32: (0x00000000, 0x80000000, 0x3F800000, 0xBF800000, 0x00000001, 0x80000001,
         0x00800000, 0x7F7FFFFF, 0xFF7FFFFF, 0x7F800000, 0xFF800000, 0x7FC00001, 0xFFC00002),
    64: (0x0000000000000000, 0x8000000000000000, 0x3FF0000000000000, 0xBFF0000000000000,
         0x0000000000000001, 0x8000000000000001, 0x0010000000000000, 0x7FEFFFFFFFFFFFFF,
         0xFFEFFFFFFFFFFFFF, 0x7FF0000000000000, 0xFFF0000000000000, 0x7FF8000000000001,
         0xFFF8000000000002),
}


def observations():
    for width, bits in BITS.items():
        for left_bits in bits:
            for right_bits in bits:
                left = struct.unpack(">f" if width == 32 else ">d", left_bits.to_bytes(width // 8, "big"))[0]
                right = struct.unpack(">f" if width == 32 else ">d", right_bits.to_bytes(width // 8, "big"))[0]
                yield {"width": width, "leftBits": format(left_bits, "0%dx" % (width // 4)),
                       "rightBits": format(right_bits, "0%dx" % (width // 4)),
                       "equal": left == right, "notEqual": left != right,
                       "less": left < right, "lessOrEqual": left <= right,
                       "greater": left > right, "greaterOrEqual": left >= right}


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "float-comparisons":
        raise ValueError("Floating-comparison report has the wrong version, stage or profile")
    expected = list(observations())
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Floating comparisons differ from the independent IEEE oracle")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "resultChecks": len(expected) * 6,
            "methods": 12, "platform": report["platform"], "profile": "float-comparisons",
            "scope": "finite scalar comparisons; no signaling-NaN exceptions or altered control-state claim"}
