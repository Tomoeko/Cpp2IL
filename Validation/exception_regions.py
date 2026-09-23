"""Independent oracle for a typed catch and a finally side effect."""

import json


def _quotient(dividend, divisor):
    value = abs(dividend) // abs(divisor)
    return value if (dividend < 0) == (divisor < 0) else -value


def _int32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def observations():
    cases = (
        (100, 5, 0),
        (0, 0, 1),
        (1, 0, -1),
        (-40, 2, 17),
        (40, -3, (1 << 31) - 2),
        (-17, -5, -(1 << 31)),
        (12, 1, (1 << 31) - 1),
    )
    rows = []
    for dividend, divisor, initial in cases:
        for method in ("catch", "finally"):
            if divisor == 0:
                result = -17 if method == "catch" else None
                exception = "none" if method == "catch" else "System.DivideByZeroException"
            else:
                result = _quotient(dividend if method == "catch" else 100, divisor)
                exception = "none"
            counter = initial if method == "catch" else _int32(initial + 1)
            rows.append({"method": method, "dividend": dividend, "divisor": divisor,
                         "initial": initial, "counter": counter,
                         "result": result, "exception": exception})
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "exception-regions":
        raise ValueError("Exception-region report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Exception-region native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Exception-region behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "exception-regions",
            "scope": "typed catch and finally counter; not general native EH recovery"}
