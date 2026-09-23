"""Independent finite Single-bit oracle for values retained across a managed call."""

import json
import struct


VECTORS = ((-17, 1, 2, 3), (0, 0, 0, 0), (1, 2, 3, 4),
           (17, -17, 17, -17), (65536, 1, -65536, 2))


def bits(value):
    return format(struct.unpack("<I", struct.pack("<f", value))[0], "08x")


def observations():
    for first, second, third, fourth in VECTORS:
        yield {"firstBits": bits(first), "secondBits": bits(second),
               "thirdBits": bits(third), "fourthBits": bits(fourth),
               "resultBits": bits(first + second + third + fourth)}


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "xmm-spill"):
        raise ValueError("XMM-spill report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("XMM-spill native observations require a Windows player")
    expected = list(observations())
    if report.get("observations") != expected:
        raise ValueError("XMM-spill behavior differs from the independent Single-bit oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "xmm-spill",
            "scope": "finite exact Single sums across a managed call"}
