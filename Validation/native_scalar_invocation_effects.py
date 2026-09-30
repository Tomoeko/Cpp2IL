"""Independent bit and state oracle for ordered scalar invocation effects."""

from behavior_oracle import verify_report


def node(calls=0, flushes=0, value=False):
    return {"calls": calls, "flushes": flushes, "value": value}


def observations():
    rows = [
        {"kind": "declarations", "methods": 6, "fields": 6,
         "markerType": "System.Single", "parameterType": "System.Boolean", "flushParameters": 0},
        {"kind": "defaults", "markerBits": 0, "node": node()},
    ]
    for operation in range(2):
        for value in (False, True):
            for kind in ("target-null", "other-null", "distinct", "alias"):
                first = node(7, 11, not value)
                second = first if kind == "alias" else node(-13, 17, value)
                exception, marker = "none", 0x3f800000
                if kind == "target-null":
                    exception = "System.NullReferenceException"
                else:
                    first.update(calls=8, value=value)
                    # The field write follows Set and precedes the optional Flush.
                    # Compare signed Int32 bits, including the sign of zero.
                    marker = 0x3f000000 if operation == 0 else -2147483648
                    if operation == 1:
                        if kind == "other-null":
                            exception = "System.NullReferenceException"
                        else:
                            second["flushes"] += 1
                rows.append({"kind": kind, "operation": operation, "value": value,
                             "exception": exception, "markerBits": marker, "alias": kind == "alias",
                             "targetNull": kind == "target-null", "otherNull": kind == "other-null",
                             "first": first.copy(), "second": second.copy()})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-scalar-invocation-effects", observations(), 6,
                         "typed Boolean invocation followed by exact Single bit-pattern stores and optional "
                         "zero-argument invocation; aliases and early/late null failure effects; finite observed states")
