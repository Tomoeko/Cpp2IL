"""Independent oracle for low-byte bitmask predicates."""

import json


MASKS = (("two", 2), ("four", 4), ("eight", 8),
         ("sixteen", 16), ("thirtyTwo", 32), ("sixtyFour", 64))


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (expected_platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "byte-mask-parameter" or
            report.get("platform") != expected_platform):
        raise ValueError("Byte-mask report has the wrong version, stage or platform")

    expected = []
    for bits in range(256):
        row = {"bits": bits}
        row.update({name: (bits & mask) != 0 for name, mask in MASKS})
        expected.append(row)
    observations = report.get("observations")
    if (not isinstance(observations, list) or
            any(not isinstance(item, dict) or type(item.get("bits")) is not int or
                any(type(item.get(name)) is not bool for name, _ in MASKS)
                for item in observations) or observations != expected):
        raise ValueError("Byte-mask observations differ from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": len(MASKS),
            "platform": expected_platform, "profile": "byte-mask-parameter"}
