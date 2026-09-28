"""Independent behavior oracle for a Boolean array owned by a derived class."""

import json

from field_boolean_array import observations


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "constructed-base-boolean-array" or
            report.get("platform") != platform):
        raise ValueError("Constructed-base Boolean-array report has the wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Constructed-base Boolean-array behavior differs from the oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "constructed-base-boolean-array",
            "scope": "fieldless constructed base, Boolean stores, null and bounds failures, aliasing"}
