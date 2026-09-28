"""Independent behavior oracle for Boolean-array literal stores through a field."""

import json


MINIMUM = -(1 << 31)
MAXIMUM = (1 << 31) - 1
SCENARIOS = (
    ("null-owner", True, None),
    ("null-array", False, None),
    ("empty", False, ()),
    ("single-false", False, (False,)),
    ("single-true", False, (True,)),
    ("mixed", False, (True, False, True)),
)


def observations():
    expected = []
    for label, missing_owner, initial in SCENARIOS:
        length = len(initial) if initial is not None else 0
        indices = (MINIMUM, -1, 0, 1, length - 1, length, MAXIMUM)
        for index in indices:
            for kind, value in (("set-true", True), ("set-false", False)):
                before = list(initial) if initial is not None else None
                after = list(initial) if initial is not None else None
                if missing_owner or initial is None:
                    exception = "System.NullReferenceException"
                elif index < 0 or index >= length:
                    exception = "System.IndexOutOfRangeException"
                else:
                    exception = "none"
                    after[index] = value

                expected.append({
                    "kind": kind, "case": label, "index": index,
                    "exception": exception, "before": before, "after": after,
                    "aliasAfter": after if not missing_owner else None,
                    "sameFieldReference": not missing_owner,
                    "aliasSharesArray": not missing_owner and initial is not None,
                    "ownerBefore": None if missing_owner else -41,
                    "ownerAfter": None if missing_owner else 73,
                    "aliasBefore": None if missing_owner else 17,
                    "aliasAfterField": None if missing_owner else -19,
                })
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "field-boolean-array" or
            report.get("platform") != platform):
        raise ValueError("Boolean-field-array report has the wrong version, stage, profile or platform")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Boolean-field-array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "profile": "field-boolean-array",
            "scope": "Boolean literal array stores, null and bounds failures, aliasing and unchanged fields"}
