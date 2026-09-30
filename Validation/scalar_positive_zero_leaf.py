"""Independent bit and declaration oracle for scalar positive-zero leaves."""

from behavior_oracle import verify_report


def observations():
    expected = [{"kind": f"{owner}-{precision}",
                 "bits": "00000000" if precision == "single" else "0000000000000000"}
                for owner in ("static", "receiver", "alias", "second")
                for precision in ("single", "double")]
    expected.extend({"kind": f"null-{precision}", "exception": "System.NullReferenceException"}
                    for precision in ("single", "double"))
    expected.extend([
        {"kind": "negative-single-control", "bits": "80000000"},
        {"kind": "negative-double-control", "bits": "8000000000000000"},
    ])
    expected.extend({"kind": "declaration", "owner": f"ScalarPositiveZeroLeafFixture.{owner}",
                     "name": name, "returnType": f"System.{precision}",
                     "static": owner == "PositiveZeroStatics", "virtual": False, "parameters": 0}
                    for owner in ("PositiveZeroStatics", "PositiveZeroReceiver")
                    for name, precision in (("SingleZero", "Single"), ("DoubleZero", "Double")))
    expected.extend([
        {"kind": "receiver-constructors", "count": 1},
        {"kind": "receiver-alias", "same": True, "secondIsDistinct": True},
    ])
    return expected


def verify(path, stage, version):
    return verify_report(path, stage, version, "scalar-positive-zero-leaf", observations(), 5,
                         "positive and negative zero bits, null calls, receiver aliases and declarations")
