"""Independent ordered array exception, call, store and alias observations."""

from behavior_oracle import verify_report


def _i32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def _exception(values, index):
    if values is None:
        return "System.NullReferenceException"
    return "System.IndexOutOfRangeException" if index < 0 or index >= len(values) else "none"


def _scalar(kind, operation, before, read=0, write=0, value=0, missing=False):
    after = None if before is None else list(before)
    result, counter, observed = None, 10, 17
    error = "System.NullReferenceException" if missing else "none"
    if error == "none" and operation != "mark":
        error = _exception(before, read)
    if error == "none":
        if operation == "twice":
            counter += 1
            result = _i32(before[read] * 2)
        elif operation == "set":
            observed = before[read]
            error = _exception(before, write)
            if error == "none":
                after[write] = value
                result = before[read]
        else:
            counter += 1
    return {"kind": kind, "operation": operation, "read": read, "write": write,
            "value": value, "result": result, "exception": error, "before": before, "after": after,
            "counter": None if missing else counter, "observed": None if missing else observed,
            "sameArray": None if missing else True, "aliasSame": True, "aliasAfter": after,
            "aliasCounter": 31, "aliasObserved": 37}


def _sum(kind, left, right, index, alias=False):
    error = _exception(left, index)
    if error == "none":
        error = _exception(right, index)
    result = _i32(left[index] + right[index]) if error == "none" else None
    return {"kind": kind, "index": index, "result": result, "exception": error,
            "leftBefore": left, "leftAfter": left, "rightBefore": right, "rightAfter": right,
            "sameArray": alias}


def observations():
    rows = [{"kind": "declarations", "owner": True, "node": True, "signatures": True},
            {"kind": "constructors", "valuesNull": True, "nodesNull": True, "counter": 0,
             "observed": 0, "nodeValue": 0, "nodeCalls": 0}]
    seed = [4, -7, 2147483647]
    for index in (-2147483648, -1, 0, 1, 2, 3, 2147483647):
        rows.append(_scalar("twice-" + str(index), "twice", seed, read=index))
    rows.extend([_scalar("twice-empty", "twice", []), _scalar("twice-null", "twice", None),
                 _scalar("twice-null-holder", "twice", seed, missing=True),
                 _scalar("set-distinct", "set", seed, 0, 2, 99),
                 _scalar("set-same", "set", seed, 1, 1, -44),
                 _scalar("set-write-failure", "set", seed, 0, 3, 99),
                 _scalar("set-read-failure", "set", seed, 3, 0, 99),
                 _scalar("set-negative-write", "set", seed, 2, -1, 99),
                 _scalar("set-null", "set", None, 0, 0, 99),
                 _scalar("set-null-holder", "set", seed, 0, 0, 99, True),
                 _scalar("mark", "mark", seed)])
    for index in (-1, 0, 1, 2, 3):
        rows.append(_sum("sum-" + str(index), seed, [6, 8, 1], index))
    rows.extend([_sum("sum-alias", seed, seed, 1, True), _sum("sum-null-left", None, [], 0),
                 _sum("sum-null-right", [4], None, 0),
                 _sum("sum-left-bounds-before-null-right", [4], None, 1),
                 _sum("sum-right-empty", [4], [], 0)])
    node_value, node_calls = 7, 2
    for kind, size, index, value, missing in (
            ("node-first", 3, 0, 11, False), ("node-alias", 3, 2, -9, False),
            ("node-null-element", 3, 1, 22, False), ("node-negative", 3, -1, 22, False),
            ("node-bounds", 3, 3, 22, False), ("node-empty", 0, 0, 22, False),
            ("node-null-array", None, 0, 22, False), ("node-null-holder", 3, 0, 22, True)):
        before_values = None if size is None else [] if size == 0 else [node_value, None, node_value]
        before_calls = None if size is None else [] if size == 0 else [node_calls, None, node_calls]
        error = "System.NullReferenceException" if missing else _exception(before_values, index)
        if error == "none" and index == 1:
            error = "System.NullReferenceException"
        if error == "none":
            node_value, node_calls = value, node_calls + 1
        rows.append({"kind": kind, "index": index, "value": value, "exception": error,
                     "beforeValues": before_values,
                     "afterValues": None if size is None else [] if size == 0 else [node_value, None, node_value],
                     "beforeCalls": before_calls,
                     "afterCalls": None if size is None else [] if size == 0 else [node_calls, None, node_calls],
                     "witnessValue": node_value, "witnessCalls": node_calls,
                     "counter": None if missing else 11 if error == "none" else 10,
                     "observed": None if missing else 17, "sameArray": None if missing else True,
                     "aliasedElements": None if size is None or size < 3 else True})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "guarded-array-operations", observations(), 9,
                         "ordered reference and Int32 accesses, aliases, calls, effects and null/bounds failures")
