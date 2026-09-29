"""Independent branch and field-side-effect oracle over finite IEEE bit patterns."""

import json
import struct

from float_comparison import BITS


def observations():
    for width, bits in BITS.items():
        for left_bits in bits:
            for right_bits in bits:
                left = struct.unpack(">f" if width == 32 else ">d", left_bits.to_bytes(width // 8, "big"))[0]
                right = struct.unpack(">f" if width == 32 else ">d", right_bits.to_bytes(width // 8, "big"))[0]
                updated = left > right
                yield {"width": width, "leftBits": format(left_bits, "0%dx" % (width // 4)),
                       "rightBits": format(right_bits, "0%dx" % (width // 4)),
                       "resultBits": format(left_bits if updated else right_bits, "0%dx" % (width // 4)),
                       "updates": 18 if updated else 17}
    for width in (32, 64):
        yield {"kind": "null-receiver", "width": width, "exception": "System.NullReferenceException"}


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "scalar-field-comparison" or
            report.get("platform") != platform):
        raise ValueError("Scalar field comparison report has the wrong version, stage or platform")
    expected = list(observations())
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Scalar field branch or side effects differ from the independent IEEE oracle")
    return {"status": "passed", "observations": len(expected), "resultChecks": len(expected) * 2 - 2,
            "methods": 3, "platform": platform, "profile": "scalar-field-comparison",
            "scope": "finite quiet-NaN comparisons and conditional field updates; no altered control-state claim"}
