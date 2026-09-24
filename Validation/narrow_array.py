"""Independent oracle for byte and signed-byte array element access."""

import json


INDICES = (-(1 << 31), -1, 0, 1, 3, 4, (1 << 31) - 1)
ARRAYS = (
    ("byte", (("empty", []), ("single", [255]),
              ("mixed", [0, 127, 128, 255]), ("null", None)),
     (0, 127, 128, 255)),
    ("signed", (("empty", []), ("single", [-128]),
                ("mixed", [-128, -1, 0, 127]), ("null", None)),
     (-128, -1, 0, 127)),
)


def observations():
    expected = []
    for prefix, arrays, write_values in ARRAYS:
        for label, values in arrays:
            for index in INDICES:
                if values is None:
                    result, exception = None, "System.NullReferenceException"
                elif index < 0 or index >= len(values):
                    result, exception = None, "System.IndexOutOfRangeException"
                else:
                    result, exception = values[index], "none"
                expected.append({
                    "kind": prefix + "-read", "array": label, "index": index,
                    "result": result, "exception": exception,
                    "valuesAfter": values,
                })

            for index in INDICES:
                for value in write_values:
                    if values is None:
                        after, exception, read_back = None, "System.NullReferenceException", None
                    elif index < 0 or index >= len(values):
                        after, exception, read_back = values[:], "System.IndexOutOfRangeException", None
                    else:
                        after = values[:]
                        after[index] = value
                        exception, read_back = "none", value
                    expected.append({
                        "kind": prefix + "-write", "array": label, "index": index,
                        "value": value, "exception": exception,
                        "readBack": read_back, "readBackException": "none",
                        "valuesBefore": values, "valuesAfter": after,
                    })
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "narrow-array" or
            report.get("platform") != platform):
        raise ValueError("Narrow-array report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Narrow-array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "narrow-array",
            "scope": "byte and signed-byte array reads/writes, aliasing, unchanged neighbors, null and bounds failures"}
