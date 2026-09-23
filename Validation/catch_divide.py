"""Independent bounded catch oracle for the exact-version native round trip."""

import json

from exception_regions import observations as exception_observations


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "catch-divide":
        raise ValueError("Catch-divide report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Catch-divide native observations require a Windows player")
    expected = [row for row in exception_observations() if row["method"] == "catch"]
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Catch-divide behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": report["platform"], "profile": "catch-divide",
            "scope": "typed catch on signed division; not general native EH recovery"}
