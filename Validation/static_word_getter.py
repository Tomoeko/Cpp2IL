"""Independent oracle for mutable signed and unsigned static 32-bit getters."""

import json


STAGES = (
    ("initial", 0, 0, 0),
    ("signed-minimum", -(1 << 31), 0, 101),
    ("signed-repeat", -(1 << 31), 0, 101),
    ("unsigned-maximum", -(1 << 31), (1 << 32) - 1, -202),
    ("signed-maximum", (1 << 31) - 1, (1 << 32) - 1, -202),
    ("unsigned-high-bit", (1 << 31) - 1, 1 << 31, 303),
    ("mixed", -1, 1, 303),
    ("signed-zero", 0, 1, -404),
    ("unsigned-zero", 0, 0, -404),
    ("final-repeat", 0, 0, -404),
)


def observations():
    return [
        {"kind": kind, "signed": signed, "unsigned": unsigned,
         "signedAgain": signed, "unsignedAgain": unsigned,
         "storedSigned": signed, "storedUnsigned": unsigned,
         "neighborBefore": neighbor, "neighborAfter": neighbor}
        for kind, signed, unsigned, neighbor in STAGES
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "static-word-getter" or
            report.get("platform") != platform):
        raise ValueError("Static-word-getter report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Static-word-getter behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "static-word-getter",
            "scope": "32-bit signed/unsigned defaults and boundaries, repeated reads, unchanged static state"}
