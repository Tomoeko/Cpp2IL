"""Independent bit-pattern oracle for three-field scalar-float reference mutation."""

import json


# Input encodings and expected left-to-right Single sums in the Release player.
# The expected results are fixed independently of the recovered implementation
# or a Python float cast.
# NaN payloads are classified because their exact output payload is not a
# portable consequence of the C# expression.
VECTORS = (
    (("3f800000", "40000000", "40400000", "40800000"), "41200000"),
    (("80000000", "80000000", "80000000", "80000000"), "80000000"),
    (("00000000", "80000000", "00000000", "80000000"), "00000000"),
    (("00000001", "00000000", "00000000", "00000000"), "00000001"),
    (("80000001", "80000000", "80000000", "80000000"), "80000001"),
    (("4b800000", "3f800000", "cb800000", "3f800000"), "3f800000"),
    (("7f800000", "3f800000", "40000000", "40400000"), "7f800000"),
    (("ff800000", "bf800000", "40000000", "40400000"), "ff800000"),
    (("7f800000", "ff800000", "00000000", "00000000"), "nan"),
    (("7fc00001", "3f800000", "40000000", "40400000"), "nan"),
    (("3f800000", "40000000", "7fc12345", "40400000"), "nan"),
    (("00000000", "00000001", "00000001", "80000001"), "00000001"),
    (("3f800000", "33800000", "33800000", "00000000"), "3f800000"),
)

# The exact Windows editor produces these two results, consistent with extra
# intermediate precision: (2**24 + 1 - 2**24 + 1) == 2, and
# (1 + 2**-24 + 2**-24) rounds to the next Single above one. This records
# editor behavior without assuming a particular JIT implementation.
EDITOR_SUM_OVERRIDES = {5: "40000000", 12: "3f800001"}


def classified(bits):
    raw = int(bits, 16)
    return "nan" if raw & 0x7F800000 == 0x7F800000 and raw & 0x007FFFFF else bits


def observations(stage):
    return [{
        "inputBits": list(inputs),
        "identityResult": classified(inputs[0]),
        "identityAfterBits": ["00000000"] * 3,
        "sumResult": EDITOR_SUM_OVERRIDES.get(index, expected_sum)
        if stage == "editor" else expected_sum,
        "sumInputAfterBits": list(inputs[1:]),
    } for index, (inputs, expected_sum) in enumerate(VECTORS)]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "xmm-ref-mutation"):
        raise ValueError("Scalar-float reference-mutation report has the wrong version, stage or profile")
    platforms = {"editor": "WindowsEditor", "player": "WindowsPlayer"}
    if stage not in platforms or report.get("platform") != platforms[stage]:
        raise ValueError("Scalar-float reference-mutation observations require the exact Windows editor or player")
    expected = observations(stage)
    if report.get("observations") != expected:
        raise ValueError("Scalar-float reference-mutation behavior differs from the independent bit oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "xmm-ref-mutation",
            "scope": "finite vectors, zeros, subnormals and infinities by exact bits; quiet NaNs by class"}
