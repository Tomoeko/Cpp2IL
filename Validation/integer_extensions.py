"""Independent bit-pattern oracle for widening integer conversions."""

import json


WORD_BITS = (0, 1, 0x7f, 0x80, 0xff, 0x100, 0x7fff, 0x8000, 0x8001, 0xff00, 0xff7f, 0xffff)
DWORD_BITS = (0, 1, 0x7fff, 0x8000, 0xffff, 0x10000, 0x7fffffff, 0x80000000,
              0x80000001, 0xffff0000, 0xfffffffe, 0xffffffff)


def bits(value, width):
    return format(value % (1 << width), "0" + str(width // 4) + "x")


def observations():
    expected = []
    for width, values in ((8, range(256)), (16, WORD_BITS), (32, DWORD_BITS)):
        for value in values:
            signed = value if value < 1 << (width - 1) else value - (1 << width)
            row = {"width": width, "inputBits": bits(value, width),
                   "sign64": bits(signed, 64), "zero64": bits(value, 64)}
            if width < 32:
                row.update(sign32=bits(signed, 32), zero32=bits(value, 32))
            if width == 8:
                row["signU32"] = bits(signed, 32)
            if width == 32:
                row["signU64"] = bits(signed, 64)
            expected.append(row)
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "integer-extensions"):
        raise ValueError("Integer extension report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Integer extension native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Integer extension behavior differs from the independent bit-pattern oracle")
    return {"status": "passed", "observations": len(expected),
            "resultChecks": sum(len(row) - 2 for row in expected), "methods": 12,
            "platform": report["platform"], "profile": "integer-extensions",
            "scope": "all byte values and selected word/dword widening boundaries; not whole-program equivalence"}
