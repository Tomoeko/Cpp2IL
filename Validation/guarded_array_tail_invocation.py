"""Ordered array failures, terminal managed calls and caller-visible effects."""

from behavior_oracle import verify_report


def _observe(kind, operation, index, argument, setup):
    calls = [7, None if setup == "null-element" else 11, 13]
    values = [4, None if setup == "null-element" else -3, 2147483647]
    missing_owner = setup == "null-owner"
    marker = None if missing_owner else 5
    error, result = "none", None
    if missing_owner and operation != "parameter":
        error = "System.NullReferenceException"
    elif setup == "null-array":
        error = "System.NullReferenceException"
    elif setup == "empty" or index < 0 or index >= 3:
        error = "System.IndexOutOfRangeException"
    elif calls[index] is None:
        error = "System.NullReferenceException"
    if not missing_owner and operation in ("marker", "produced"):
        marker += 1
    if error == "none":
        calls[index] += 1
        if operation == "accept":
            values[index] = argument
        else:
            result = values[index]
    return {"kind": kind, "operation": operation, "index": index, "argument": argument,
            "setup": setup, "result": result, "exception": error, "marker": marker,
            "firstCalls": calls[0], "firstValue": values[0],
            "secondCalls": calls[1], "secondValue": values[1],
            "thirdCalls": calls[2], "thirdValue": values[2],
            "arraySame": None if missing_owner else True}


def observations():
    rows = [{"kind": "declarations", "owners": True, "signatures": True, "fields": True}]
    for operation in ("read", "accept", "marker", "produced", "parameter"):
        for index in (-2147483648, -1, 0, 1, 2, 3, 2147483647):
            rows.append(_observe(operation + "-" + str(index), operation, index, -2147483648, "ordinary"))
        for setup, index in (("null-array", 0), ("empty", 0), ("null-element", 1), ("null-owner", 0)):
            rows.append(_observe(operation + "-" + setup, operation, index, 99, setup))
    rows.extend([_observe("accept-max", "accept", 2, 2147483647, "ordinary"),
                 {"kind": "constructors", "arrayNull": True, "marker": 0, "calls": 0, "value": 0}])
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "guarded-array-tail-invocation", observations(), 10,
                         "original checked element receivers and scalar arguments, balanced terminal calls, "
                         "ordered caller effects and null/bounds failures")
