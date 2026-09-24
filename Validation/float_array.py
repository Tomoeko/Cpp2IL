"""Independent bit-pattern oracle for float array element reads and writes."""

import json


def signed32(bits):
    return bits if bits < 0x80000000 else bits - 0x100000000


INDICES = (-(1 << 31), -1, 0, 1, 4, 9, 10, (1 << 31) - 1)
VALUE_BITS = tuple(map(signed32, (
    0x00000000, 0x80000000, 0x7F800000, 0xFF800000,
    0x7FC00001, 0x7FC12345, 0x00000001, 0x007FFFFF,
    0x80000001, 0x807FFFFF,
)))
ARRAYS = (
    ("empty", []),
    ("single", [signed32(0x7FC00001)]),
    ("mixed", list(VALUE_BITS)),
    ("null", None),
)


def observations():
    expected = []
    for label, values in ARRAYS:
        for index in INDICES:
            if values is None:
                result_bits, exception = None, "System.NullReferenceException"
            elif index < 0 or index >= len(values):
                result_bits, exception = None, "System.IndexOutOfRangeException"
            else:
                result_bits, exception = values[index], "none"
            expected.append({
                "kind": "float-read", "array": label, "index": index,
                "resultBits": result_bits, "exception": exception,
                "valuesAfterBits": values,
            })

        for index in INDICES:
            for value_bits in VALUE_BITS:
                if values is None:
                    after, exception, read_back = None, "System.NullReferenceException", None
                elif index < 0 or index >= len(values):
                    after, exception, read_back = values[:], "System.IndexOutOfRangeException", None
                else:
                    after = values[:]
                    after[index] = value_bits
                    exception, read_back = "none", value_bits
                expected.append({
                    "kind": "float-write", "array": label, "index": index,
                    "valueBits": value_bits, "exception": exception,
                    "readBackBits": read_back, "readBackException": "none",
                    "valuesBeforeBits": values, "valuesAfterBits": after,
                })
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "float-array" or
            report.get("platform") != platform):
        raise ValueError("Float-array report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Float-array behavior differs from the independent bit-pattern oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "float-array",
            "scope": "float array bit patterns, aliasing, unchanged neighbors, null and bounds failures"}
