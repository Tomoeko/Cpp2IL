"""Independent bit arithmetic for low/high halves of complete 64-bit values."""

import json


PATTERNS = (0, 1, 0x7fffffff, 0x80000000, 0xffffffff, 0x100000000,
            0x7fffffff00000000, 0x8000000000000000, 0x80000000ffffffff,
            0xffffffff00000000, 0xffffffffffffffff, 0x123456789abcdef0)


def signed32(value):
    return (value + 0x80000000) % 0x100000000 - 0x80000000


def expected_observations():
    observations = []
    for operation in ("lowSigned", "lowUnsigned", "highSigned", "highUnsigned", "splitSigned", "splitUnsigned"):
        for bits in PATTERNS:
            for initial in range(3 if operation.startswith("split") else 1):
                low, high = bits & 0xffffffff, bits >> 32
                signed_initial = (0, -0x80000000, 0x7fffffff)[initial]
                unsigned_initial = (0, 0x80000000, 0xffffffff)[initial]
                signed_low, signed_high = signed_initial, signed_initial
                unsigned_low, unsigned_high = unsigned_initial, unsigned_initial
                result = 0
                if operation == "lowSigned": result = signed32(low)
                elif operation == "lowUnsigned": result = low
                elif operation == "highSigned": result = signed32(high)
                elif operation == "highUnsigned": result = high
                elif operation == "splitSigned": signed_low, signed_high = signed32(low), signed32(high)
                else: unsigned_low, unsigned_high = low, high
                observations.append({"operation": operation, "bits": bits, "initial": initial, "result": result,
                                     "signedLow": signed_low, "signedHigh": signed_high,
                                     "unsignedLow": unsigned_low, "unsignedHigh": unsigned_high})
    return observations


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("platform") != platform or report.get("profile") != "integer-truncation"):
        raise ValueError("Integer truncation report has the wrong target or stage")
    expected = expected_observations()
    actual = report.get("observations")
    numeric = ("bits", "initial", "result", "signedLow", "signedHigh", "unsignedLow", "unsignedHigh")
    if (not isinstance(actual, list) or any(not isinstance(row, dict) or
            any(type(row.get(field)) is not int for field in numeric) for row in actual) or actual != expected):
        raise ValueError("Integer truncation observations differ from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 7,
            "platform": platform, "profile": "integer-truncation"}
