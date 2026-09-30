"""Independent exact-target oracle for one conditional managed throw."""

from behavior_oracle import verify_report


def _row(kind, fail, value, result, exception):
    return {"kind": kind, "fail": fail, "value": value,
            "result": result, "exception": exception}


def observations():
    thrown = "System.NotSupportedException"
    return [
        _row("zero", False, 0, 1, "none"),
        _row("negative", False, -2, -1, "none"),
        _row("positive-edge", False, 2**31 - 1, -(2**31), "none"),
        _row("throw-zero", True, 0, None, thrown),
        _row("throw-negative-edge", True, -(2**31), None, thrown),
        _row("negative-edge", False, -(2**31), -(2**31) + 1, "none"),
        _row("throw-positive-edge", True, 2**31 - 1, None, thrown),
        _row("after-throw", False, 37, 38, "none"),
    ]


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "conditional-managed-throw", observations(), 1,
        "normal return, signed overflow, exact throw type, and post-throw reuse")
