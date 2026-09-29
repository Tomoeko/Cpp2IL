"""Independent IEEE component selection and by-value preservation oracle."""

import json
import struct
from float_comparison import BITS


def observations():
    bits = BITS[32]
    for kind in ("static-low", "static-high", "instance-low", "instance-high",
                 "instance-low-compare", "instance-high-compare"):
        for left, left_bits in enumerate(bits):
            for right, right_bits in enumerate(bits):
                left_other = bits[(left + 3) % len(bits)]
                right_other = bits[(right + 5) % len(bits)]
                left_value = struct.unpack(">f", left_bits.to_bytes(4, "big"))[0]
                right_value = struct.unpack(">f", right_bits.to_bytes(4, "big"))[0]
                low = "-low" in kind
                if kind.endswith("-compare"):
                    result = 1 if left_value > right_value else -1 if left_value < right_value else 0
                else:
                    result = left_value > right_value
                yield {"kind": kind, "leftBits": format(left_bits, "08x"), "rightBits": format(right_bits, "08x"),
                       "leftOtherBits": format(left_other, "08x"), "rightOtherBits": format(right_other, "08x"),
                       "result": result,
                       "firstLowAfter": format(left_bits if low else left_other, "08x"),
                       "firstHighAfter": format(left_other if low else left_bits, "08x"),
                       "secondLowAfter": format(right_bits if low else right_other, "08x"),
                       "secondHighAfter": format(right_other if low else right_bits, "08x")}
    for kind in ("null-low-compare", "null-high-compare"):
        yield {"kind": kind, "exception": "System.NullReferenceException"}


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "aggregate-scalar-compare" or report.get("platform") != platform):
        raise ValueError("Aggregate scalar report has the wrong version, stage or platform")
    expected = list(observations())
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Aggregate component selection or by-value fields differ from the independent IEEE oracle")
    return {"status": "passed", "observations": len(expected), "resultChecks": (len(expected) - 2) * 5 + 2,
            "methods": 7, "platform": platform, "profile": "aggregate-scalar-compare",
            "scope": "two-Single by-value Boolean/three-way comparisons, quiet NaNs, caller preservation and null call sites; no packed SIMD or altered control-state claim"}
