"""Independent Int32, length and ordered-null-failure oracle."""

import json


def signed32(value):
    return (value + 2147483648) % 4294967296 - 2147483648


def expected_observations():
    observations = []
    for operation in ("parameter", "objects", "copy", "advance", "last"):
        for length in (-1, 0, 1, 3, 7):
            for index in (-2147483648, -1, 0, 3, 2147483647):
                for marker in (-1, 2147483647):
                    result, index_after, marker_after = -73, index, marker
                    if operation in ("parameter", "objects", "copy"):
                        marker_after = signed32(marker + 1)
                    else:
                        index_after = signed32(index + 1)
                    exception = "NullReferenceException" if length < 0 else "none"
                    if length >= 0:
                        if operation in ("parameter", "objects"):
                            result = length
                        elif operation == "copy":
                            index_after = length
                        else:
                            reset = index_after >= length if operation == "advance" else index_after > length - 1
                            if reset:
                                index_after = 0
                            marker_after = signed32(marker + (1 if operation == "advance" else 2))
                    observations.append({"operation": operation, "length": length, "index": index, "marker": marker,
                                         "result": result, "indexAfter": index_after, "markerAfter": marker_after,
                                         "exception": exception})
    return observations


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("platform") != platform or report.get("profile") != "guarded-array-length"):
        raise ValueError("Array length report has the wrong target or stage")
    expected = expected_observations()
    if report.get("observations") != expected:
        raise ValueError("Array length observations differ from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 6,
            "platform": platform, "profile": "guarded-array-length"}
