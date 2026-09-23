"""Independent oracle for signed/unsigned 32/64-bit array accesses."""

import json


def observations():
    profiles = (
        ("", "write:", (("empty", []), ("single", [-(1 << 31)]),
                       ("mixed", [-7, 0, 19, (1 << 31) - 1]), ("null", None)),
         (1 << 31) - 1, -(1 << 31)),
        ("unsigned:", "unsigned-write:", (("empty", []), ("single", [(1 << 32) - 1]),
                                          ("mixed", [0, 1, 1 << 31, (1 << 32) - 1]), ("null", None)),
         (1 << 32) - 1, 1 << 31),
        ("wide:", "wide-write:", (("empty", []), ("single", [-(1 << 63)]),
                                  ("mixed", [-7, 0, 19, (1 << 63) - 1]), ("null", None)),
         (1 << 63) - 1, -(1 << 63)),
        ("wide-unsigned:", "wide-unsigned-write:", (("empty", []), ("single", [(1 << 64) - 1]),
                                                      ("mixed", [0, 1, 1 << 63, (1 << 64) - 1]), ("null", None)),
         (1 << 64) - 1, 1 << 63),
    )
    expected = []
    for read_kind, write_kind, arrays, high_value, low_value in profiles:
        for label, values in arrays:
            length = len(values) if values is not None else 0
            indices = (-(1 << 31), -1, 0, 1, length - 1, length, (1 << 31) - 1)
            for is_write in (False, True):
                for index in indices:
                    if values is None:
                        result, exception = None, "System.NullReferenceException"
                    elif index < 0 or index >= length:
                        result, exception = None, "System.IndexOutOfRangeException"
                    elif is_write:
                        result, exception = (high_value if index & 1 == 0 else low_value), "none"
                    else:
                        result, exception = values[index], "none"
                    expected.append({"kind": (write_kind if is_write else read_kind) + label,
                                     "index": index, "result": result, "exception": exception})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "array-access":
        raise ValueError("Array-access report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Array-access native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Array-access behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 8,
            "platform": report["platform"], "profile": "array-access",
            "scope": "signed/unsigned 32/64-bit array reads and writes, null and bounds failures; not whole-program equivalence"}
