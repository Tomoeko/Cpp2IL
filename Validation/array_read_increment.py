"""Independent oracle for one checked Int32 array read followed by unchecked +1."""

import json


CASES = (
    ("empty", [], (-1, 0, (1 << 31) - 1)),
    ("single-max", [(1 << 31) - 1], (-(1 << 31), -1, 0, 1, (1 << 31) - 1)),
    ("mixed", [-7, 0, 17, (1 << 31) - 1],
     (-(1 << 31), -1, 0, 1, 2, 3, 4, (1 << 31) - 1)),
    ("null", None, (-(1 << 31), -1, 0, 3, (1 << 31) - 1)),
)


def observations():
    expected = []
    for kind, values, indices in CASES:
        for index in indices:
            if values is None:
                result, exception = None, "System.NullReferenceException"
            elif index < 0 or index >= len(values):
                result, exception = None, "System.IndexOutOfRangeException"
            else:
                result = ((values[index] + 1 + (1 << 31)) % (1 << 32)) - (1 << 31)
                exception = "none"
            expected.append({"kind": kind, "index": index, "result": result,
                             "exception": exception,
                             "valuesAfter": None if values is None else list(values)})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "array-read-increment"):
        raise ValueError("Array-read-increment report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Array-read-increment native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Array-read-increment behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": report["platform"], "profile": "array-read-increment",
            "scope": "one Int32 array read followed by unchecked increment, null and bounds failures, unchanged array"}
