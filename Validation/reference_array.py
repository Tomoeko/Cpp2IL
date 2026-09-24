"""Independent identity and exception oracle for reference-array reads."""

import json


INDICES = (-(1 << 31), -1, 0, 1, 2, 3, (1 << 31) - 1)
ARRAYS = (
    ("object-read", (
        ("null", None), ("empty", []), ("single", ["object-a"]),
        ("mixed", ["null", "object-a", "object-b"]),
        ("covariant", ["text-a", "null", "text-b"]),
    )),
    ("string-read", (
        ("null", None), ("empty", []), ("single", ["text-a"]),
        ("mixed", ["null", "text-a", "text-b"]),
        ("alias", ["text-a", "text-a", "text-b"]),
    )),
    ("class-read", (
        ("null", None), ("empty", []), ("single", ["exception-a"]),
        ("mixed", ["null", "exception-a", "exception-b"]),
        ("covariant", ["argument-a", "null", "argument-b"]),
    )),
)


def observations():
    expected = []
    for kind, arrays in ARRAYS:
        for label, values in arrays:
            for index in INDICES:
                if values is None:
                    result, same_reference, exception = None, None, "System.NullReferenceException"
                elif index < 0 or index >= len(values):
                    result, same_reference, exception = None, None, "System.IndexOutOfRangeException"
                else:
                    result, same_reference, exception = values[index], True, "none"
                expected.append({
                    "kind": kind, "array": label, "index": index,
                    "result": result, "sameReference": same_reference,
                    "exception": exception, "valuesBefore": values, "valuesAfter": values,
                })
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "reference-array" or
            report.get("platform") != platform):
        raise ValueError("Reference-array report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Reference-array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "profile": "reference-array",
            "scope": "reference identity, covariance, unchanged arrays, null and bounds failures"}
