"""Independent narrow storage/sign-extension and caller-preservation oracle."""

import json


VALUES = tuple(range(256)) + (256, 257, 32766, 32767, 32768, 32769, 65534, 65535)


def signed(value, width):
    value &= (1 << width) - 1
    return value - (1 << width) if value & (1 << (width - 1)) else value


def observations():
    for operation in range(8):
        for value in VALUES:
            fields = (signed(value, 8), (value * 17 + 3) & 255,
                      signed(value, 16), value ^ 0x8001)
            yield {"operation": operation, "input": value, "result": fields[operation % 4],
                   "signedByteAfter": fields[0], "unsignedByteAfter": fields[1],
                   "signedWordAfter": fields[2], "unsignedWordAfter": fields[3],
                   "neighborAfter": 0xfedcba98}


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "narrow-scalar-getter" or
            report.get("platform") != platform):
        raise ValueError("Narrow scalar getter report has the wrong version, stage or platform")
    expected = list(observations())
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Narrow scalar result or caller storage differs from the independent integer oracle")
    return {"status": "passed", "observations": len(expected), "resultChecks": len(expected) * 6,
            "methods": 8, "platform": platform, "profile": "narrow-scalar-getter",
            "scope": "eight direct blittable struct byte/word getters and canonical 32-bit widening; no generic, enum or overlapping layout claim"}
