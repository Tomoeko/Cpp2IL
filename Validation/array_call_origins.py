"""Independent call capture, array mutation and exception-order observations."""

from behavior_oracle import verify_report


def _i32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def _failure(values, index):
    if values is None:
        return "System.NullReferenceException"
    return "System.IndexOutOfRangeException" if index < 0 or index >= len(values) else "none"


def _observe(kind, operation, left, right, index, value=0, missing=False, alias=False):
    after = None if left is None else list(left)
    other_after = None if right is None else list(right)
    calls, effects, result = 5, 11, None
    error = "System.NullReferenceException" if missing else "none"
    if error == "none":
        if operation in ("produced", "capture", "around", "pair"):
            calls += 1
        if operation == "pair":
            calls += 1
        if operation in ("produced", "around", "pair"):
            error = _failure(left, index)
        if error == "none" and operation in ("capture", "after", "around"):
            error = _failure(left, index)
            if error == "none":
                after[index] = value
                if alias:
                    other_after[index] = value
                effects += 1
        if error == "none" and operation in ("after", "around"):
            calls += 1
        if error == "none" and operation == "pair":
            error = _failure(right, index)
        if error == "none":
            result = (_i32(left[index] + right[index]) if operation == "pair" else
                      _i32(left[index] + value) if operation == "around" else
                      after[index])
    return {"kind": kind, "operation": operation, "index": index, "value": value,
            "result": result, "exception": error, "leftBefore": left, "leftAfter": after,
            "rightBefore": right, "rightAfter": other_after,
            "producerCalls": None if missing else calls, "effectCalls": None if missing else effects,
            "neighbor": None if missing else 37, "alias": alias,
            "ownerLeftSame": None if missing else True, "ownerRightSame": None if missing else True}


def observations():
    rows = [{"kind": "declarations", "owner": True, "signatures": True},
            {"kind": "constructor", "valuesNull": True, "otherNull": True,
             "producerCalls": 0, "effectCalls": 0, "neighbor": 0}]
    seed = [4, -7, 2147483647]
    other = [6, 8, 1]
    for index in (-2147483648, -1, 0, 1, 2, 3, 2147483647):
        rows.append(_observe("produced-" + str(index), "produced", seed, other, index))
    rows.extend([_observe("produced-null", "produced", None, seed, 0),
                 _observe("produced-empty", "produced", [], seed, 0),
                 _observe("produced-null-owner", "produced", seed, seed, 0, missing=True, alias=True)])
    for operation in ("capture", "after", "around"):
        rows.extend([_observe(operation + "-first", operation, seed, other, 0, 99),
                     _observe(operation + "-overflow", operation, seed, other, 2, -2147483648),
                     _observe(operation + "-negative", operation, seed, seed, -1, 99, alias=True),
                     _observe(operation + "-bounds", operation, seed, seed, 3, 99, alias=True),
                     _observe(operation + "-null", operation, None, seed, 0, 99),
                     _observe(operation + "-empty", operation, [], seed, 0, 99),
                     _observe(operation + "-null-owner", operation, seed, seed, 0, 99, True, True)])
    for index in (-1, 0, 1, 2, 3):
        rows.append(_observe("pair-" + str(index), "pair", seed, other, index))
    rows.extend([_observe("pair-null-left", "pair", None, seed, 0),
                 _observe("pair-null-right", "pair", seed, None, 0),
                 _observe("pair-left-bounds-before-null-right", "pair", seed, None, 3),
                 _observe("pair-empty-right", "pair", seed, [], 0),
                 _observe("pair-alias", "pair", seed, seed, 1, alias=True),
                 _observe("pair-null-owner", "pair", seed, seed, 0, missing=True, alias=True),
                 {"kind": "producer-aliases", "firstSame": True, "secondSame": True, "otherSame": True,
                  "producerCalls": 3, "effectCalls": 0, "neighbor": 37, "left": [4], "right": [6]}])
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "array-call-origins", observations(), 9,
                         "distinct producer results, captured arrays across effects, aliases and ordered null/bounds failures")
