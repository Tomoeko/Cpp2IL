"""Independent exhaustive oracle for the unsigned-byte low-bit predicate."""

import json


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if expected_platform is None or report.get("unityVersion") != version or \
            report.get("stage") != stage or report.get("profile") != "byte-mask-one" or \
            report.get("platform") != expected_platform:
        raise ValueError("Byte-mask-one report has the wrong version, stage or platform")

    expected = [{"unused": unused, "bits": value, "one": (value & 1) != 0}
                for unused in (-2147483648, -1, 0, 1, 2147483647)
                for value in range(256)]
    if report.get("observations") != expected:
        raise ValueError("Byte-mask-one observations differ from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": expected_platform, "profile": "byte-mask-one"}
