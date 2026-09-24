"""Independent behavior oracle for a read/write reference property."""

import json


def observations():
    checks = (
        "starts-null-field", "getter-starts-null", "distinct-cells",
        "first-field-identity", "first-getter-identity", "neighbor-untouched",
        "marker-untouched", "other-cell-untouched", "alias-field-replacement",
        "alias-getter-replacement", "old-value-replaced", "separate-cell-getter",
        "first-cell-unchanged", "separate-neighbor-marker", "self-reference-getter",
        "null-clears-field", "null-clears-getter", "neighbor-after-clear",
        "marker-after-clear",
    )
    expected = [{"subject": "reference", "check": check, "result": True}
                for check in checks]
    expected.extend({"subject": "reference", "check": check,
                     "exception": "System.NullReferenceException"}
                    for check in ("null-setter-value", "null-setter-null", "null-getter"))
    expected.extend({"subject": "text", "check": check, "result": True}
                    for check in ("starts-null", "first-field-identity",
                                  "neighbor-untouched", "replacement-field-identity",
                                  "marker-untouched", "null-clears-field",
                                  "neighbor-after-clear"))
    expected.append({"subject": "text", "check": "null-setter",
                     "exception": "System.NullReferenceException"})
    expected.extend({"subject": "declaration", "check": check, "result": True}
                    for check in ("reference-property-type", "reference-read-write",
                                  "text-property-type", "text-setter-only"))
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "instance-reference-property" or
            report.get("platform") != platform):
        raise ValueError("Instance reference property report has wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Instance reference property behavior differs from independent oracle")
    return {"status": "passed", "observations": len(expected),
            "platform": report["platform"], "profile": "instance-reference-property",
            "scope": "read/write object property identity, setter-only string property, neighboring state and null receivers"}
