"""Independent behavior oracle for the static scalar setter fixture."""

import json


def observations():
    rows = [("initial", 0, 0, 0),
            ("neighbors-set", -1234567, 0, 7654321)]
    rows.extend((phase, -1234567, value, 7654321) for phase, value in
                (("first", 17), ("repeat", 17), ("negative", -17),
                 ("minimum", -(1 << 31)), ("maximum", (1 << 31) - 1),
                 ("zero", 0)))
    return [{"phase": phase, "before": before, "value": value, "after": after}
            for phase, before, value, after in rows]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "static-scalar-setter"):
        raise ValueError("Static setter report has the wrong version, stage or profile")
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if expected_platform is None or report.get("platform") != expected_platform:
        raise ValueError("Static setter observations require the requested Windows stage")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Static setter behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": report["platform"], "profile": "static-scalar-setter",
            "scope": "signed Int32 stores, first use, repeated writes and neighboring static fields"}
