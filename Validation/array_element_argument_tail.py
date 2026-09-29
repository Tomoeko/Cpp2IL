"""Typed behavior oracle for passing an array element field to an owner method."""

from behavior_oracle import verify_report


NULL_FAILURE = "System.NullReferenceException"
BOUNDS_FAILURE = "System.IndexOutOfRangeException"


def _invoke(scenario, index):
    if scenario == "array-null":
        sentinels, values, failure, last = None, None, NULL_FAILURE, "seed"
    elif scenario == "length-zero":
        sentinels, values, failure, last = [], [], BOUNDS_FAILURE, "seed"
    else:
        sentinels, values = [17, 53], [11, 23]
        if scenario == "element-null":
            sentinels[0], values[0] = None, None
        elif scenario == "value-null":
            values[0] = None
        elif scenario == "element-alias":
            sentinels[1], values[1] = 17, 11
        elif scenario == "value-alias":
            values[1] = 11
        if index < 0 or index >= 2:
            failure, last = BOUNDS_FAILURE, "seed"
        elif scenario == "element-null" and index == 0:
            failure, last = NULL_FAILURE, "seed"
        else:
            failure = "none"
            last = "null" if scenario == "value-null" and index == 0 else (
                "first" if index == 0 or scenario in ("element-alias", "value-alias")
                else "second")
    pair = sentinels is not None and len(sentinels) == 2
    return {
        "kind": "invoke", "scenario": scenario, "index": index,
        "failure": failure, "callCount": 8 if failure == "none" else 7,
        "last": last, "sameArray": True,
        "sameElements": None if sentinels is None else True,
        "sameValues": None if sentinels is None else True,
        "elementsAliased": None if not pair else scenario == "element-alias",
        "valuesAliased": None if not pair else scenario in ("element-alias", "value-alias"),
        "itemSentinels": sentinels, "valueIds": values, "neighbor": 83,
    }


def _sequence(step, index, failure, count, last, first_id):
    return {
        "kind": "sequence", "step": step, "index": index,
        "failure": failure, "callCount": count, "last": last,
        "firstValueId": first_id, "secondValueId": 23,
        "thirdValueNull": True, "sameArray": True,
        "sameElements": True, "neighbor": 83,
    }


def observations():
    rows = [{
        "kind": "constructor", "itemsNull": True, "callCount": 0,
        "lastNull": True, "neighbor": 0, "valueNull": True,
        "itemSentinel": 0, "valueId": 0,
    }]
    for index in (-1, 0, 2):
        rows.append({"kind": "null-owner", "index": index,
                     "failure": NULL_FAILURE})
        rows.append(_invoke("array-null", index))
    for index in (-1, 0, (1 << 31) - 1):
        rows.append(_invoke("length-zero", index))
    for index in (-(1 << 31), -1, 0, 1, 2, (1 << 31) - 1):
        rows.append(_invoke("length-two", index))
    for scenario in ("element-null", "value-null", "element-alias", "value-alias"):
        for index in (0, 1):
            rows.append(_invoke(scenario, index))
    rows.extend([
        _sequence("first", 0, "none", (1 << 31) - 1, "first", 11),
        _sequence("second", 1, "none", -(1 << 31), "second", 11),
        _sequence("invalid", -1, BOUNDS_FAILURE, -(1 << 31), "second", 11),
        _sequence("null-value", 2, "none", -(1 << 31) + 1, "null", 11),
        _sequence("changed-value", 0, "none", -(1 << 31) + 2, "second", 23),
    ])
    return rows


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "array-element-argument-tail", observations(), 5,
        "Five concrete methods; array and element failures before a direct owner call, "
        "nullable argument identity, aliases, ordered repeated calls, Int32 wrap, and unchanged fields")
