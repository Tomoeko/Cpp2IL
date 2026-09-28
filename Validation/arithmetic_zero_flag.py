"""Independent wraparound and side-effect oracle for arithmetic zero flags."""

import json

INT_PAIRS = (
    (0, 0), (1, 1), (-1, -1),
    (2**31 - 1, -1), (-(2**31), 1),
    (2**31 - 1, 1), (-5, 2), (5, -2), (1, 0),
)
LONG_PAIRS = (
    (0, 0), (1, 1), (-1, -1),
    (2**63 - 1, -1), (-(2**63), 1),
    (2**63 - 1, 1), (-5, 2), (5, -2), (1, 0),
)


def _wrap(value, bits):
    limit = 1 << bits
    return (value + (limit >> 1)) % limit - (limit >> 1)


def _case(method, left, right, bits, add=False):
    stored = _wrap(left + right if add else left - right, bits)
    return {"method": method, "left": left, "right": right,
            "result": stored == 0, "stored": stored,
            "exception": "none"}


def observations():
    rows = []
    for left, right in INT_PAIRS:
        rows.append(_case("subtract32", left, right, 32))
        rows.append(_case("add32", left, right, 32, add=True))
    for left, right in LONG_PAIRS:
        rows.append(_case("subtract64", left, right, 64))
    for method in ("subtract32", "add32", "subtract64"):
        rows.append({"method": method, "left": 1,
                     "right": -1 if method == "add32" else 1,
                     "result": None, "stored": None,
                     "exception": "System.NullReferenceException"})
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "arithmetic-zero-flag" or
            report.get("platform") != platform):
        raise ValueError("Arithmetic zero-flag report has the wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Arithmetic zero-flag behavior differs from the oracle")
    return {"status": "passed", "observations": len(expected),
            "methods": 4, "platform": platform,
            "profile": "arithmetic-zero-flag"}
