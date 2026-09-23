"""Independent oracle for signed and unsigned 32-bit array accesses."""

import json


def observations():
    signed_arrays = (
        ("empty", []),
        ("single", [-(1 << 31)]),
        ("mixed", [-7, 0, 19, (1 << 31) - 1]),
        ("null", None),
    )
    expected = []
    for label, values in signed_arrays:
        length = len(values) if values is not None else 0
        for index in (-(1 << 31), -1, 0, 1, length - 1, length, (1 << 31) - 1):
            if values is None:
                result, exception = None, "System.NullReferenceException"
            elif index < 0 or index >= length:
                result, exception = None, "System.IndexOutOfRangeException"
            else:
                result, exception = values[index], "none"
            expected.append({"kind": label, "index": index, "result": result, "exception": exception})
        for index in (-(1 << 31), -1, 0, 1, length - 1, length, (1 << 31) - 1):
            if values is None:
                result, exception = None, "System.NullReferenceException"
            elif index < 0 or index >= length:
                result, exception = None, "System.IndexOutOfRangeException"
            else:
                result, exception = ((1 << 31) - 1 if index & 1 == 0 else -(1 << 31)), "none"
            expected.append({"kind": "write:" + label, "index": index,
                             "result": result, "exception": exception})
    unsigned_arrays = (
        ("empty", []),
        ("single", [(1 << 32) - 1]),
        ("mixed", [0, 1, 1 << 31, (1 << 32) - 1]),
        ("null", None),
    )
    for label, values in unsigned_arrays:
        length = len(values) if values is not None else 0
        for index in (-(1 << 31), -1, 0, 1, length - 1, length, (1 << 31) - 1):
            if values is None:
                result, exception = None, "System.NullReferenceException"
            elif index < 0 or index >= length:
                result, exception = None, "System.IndexOutOfRangeException"
            else:
                result, exception = values[index], "none"
            expected.append({"kind": "unsigned:" + label, "index": index,
                             "result": result, "exception": exception})
        for index in (-(1 << 31), -1, 0, 1, length - 1, length, (1 << 31) - 1):
            if values is None:
                result, exception = None, "System.NullReferenceException"
            elif index < 0 or index >= length:
                result, exception = None, "System.IndexOutOfRangeException"
            else:
                result, exception = ((1 << 32) - 1 if index & 1 == 0 else 1 << 31), "none"
            expected.append({"kind": "unsigned-write:" + label, "index": index,
                             "result": result, "exception": exception})
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
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "array-access",
            "scope": "signed/unsigned 32-bit array reads and writes, null and bounds failures; not whole-program equivalence"}
