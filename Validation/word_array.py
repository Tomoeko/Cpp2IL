"""Independent oracle for signed and unsigned 16-bit array widening reads."""

import json


INDICES = (-(1 << 31), -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, (1 << 31) - 1)
ARRAYS = (
    ("signed", (("empty", []), ("single", [-32768]),
                ("mixed", [-32768, -1, 0, 1, 32767, 256, -256, 12345]),
                ("null", None))),
    ("unsigned", (("empty", []), ("single", [65535]),
                  ("mixed", [0, 1, 255, 256, 32767, 32768, 65534, 65535]),
                  ("null", None))),
)


def observations():
    expected = []
    for prefix, arrays in ARRAYS:
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
                    "valuesBefore": values, "valuesAfter": values,
                })
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "word-array" or
            report.get("platform") != platform):
        raise ValueError("Word-array report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Word-array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "word-array",
            "scope": "16-bit signed/unsigned widening, unchanged arrays, null and bounds failures"}
