"""Independent wraparound, storage and receiver-failure oracle for literal stores."""

import json


def signed32(value):
    return (value + 2147483648) % 4294967296 - 2147483648


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("platform") != platform or
            report.get("profile") != "integer-literal-store"):
        raise ValueError("Integer store report has the wrong target or stage")
    expected = []
    for operation in range(6):
        for seed in (-2147483648, -1, 0, 2147483647):
            for value in (-2147483648, 0, 2147483647):
                for missing in (False, True):
                    for increment in (-2147483648, -7, 0, 2147483647):
                        marker = seed
                        if operation != 5:
                            marker = signed32(marker + 1 + (increment if operation == 1 else 0))
                            if not missing:
                                marker = signed32(marker + operation + 1)
                        signed = value
                        unsigned = value % 4294967296
                        if not missing:
                            if operation < 3:
                                signed = (-1, -2147483648, 2147483647)[operation]
                            elif operation < 5:
                                unsigned = (2147483648, 4294967295)[operation - 3]
                            else:
                                signed = 1
                        expected.append({"operation": operation, "seed": seed, "value": value,
                                         "missing": missing, "increment": increment, "marker": marker,
                                         "signedAfter": signed, "unsignedAfter": unsigned,
                                         "neighborAfter": 37, "currentMatches": True,
                                         "exception": "NullReferenceException" if missing else "none"})
    if report.get("observations") != expected:
        raise ValueError("Integer store observations differ from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 10,
            "platform": platform, "profile": "integer-literal-store"}
