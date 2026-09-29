"""Independent oracle for parameter-origin Boolean array stores."""

import json


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("platform") != platform or
            report.get("profile") != "parameter-boolean-array-store"):
        raise ValueError("Boolean array store report has the wrong target or stage")
    expected = []
    for operation in range(9):
        for shape in range(5):
            for index in (-2147483648, -1, 0, 1, 2, 3, 2147483647):
                for value in (False, True):
                    values = (None, [], [False], [True], [True, False, True])[shape]
                    exception = "none"
                    if values is None:
                        exception = "NullReferenceException"
                    elif index < 0 or index >= len(values):
                        exception = "IndexOutOfRangeException"
                    else:
                        values[index] = value if operation in (0, 4, 8) else operation in (2, 6)
                    expected.append({"operation": operation, "shape": shape, "index": index,
                                     "value": value, "exception": exception, "values": values,
                                     "length": -1 if values is None else len(values)})
    if report.get("observations") != expected:
        raise ValueError("Boolean array store observations differ from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 10,
            "platform": platform, "profile": "parameter-boolean-array-store"}
