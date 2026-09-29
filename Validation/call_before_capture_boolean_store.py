"""Independent effect-order and receiver-snapshot oracle for a nested Boolean store."""

from behavior_oracle import verify_report


COUNTERS = (-(1 << 31), -1, 0, (1 << 31) - 1)


def signed32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def observations():
    expected = [{
        "kind": "constructor", "currentNull": True, "otherNull": True,
        "counter": 0, "ownerSentinel": 0, "flag": False,
        "neighbor": False, "targetSentinel": 0,
    }, {
        "kind": "null-owner", "failure": "System.NullReferenceException",
    }]
    for counter in COUNTERS:
        for mode in range(4):
            for flag in (False, True):
                first_flag = flag or mode in (1, 3)
                second_flag = not flag or mode == 2
                expected.append({
                    "kind": "invoke", "counterBefore": counter,
                    "mode": mode, "flagBefore": flag,
                    "failure": "System.NullReferenceException" if mode == 0 else "none",
                    "counterAfter": signed32(counter + 1),
                    "currentSameFirst": mode in (1, 3),
                    "currentSameSecond": mode == 2,
                    "otherSameFirst": mode == 3,
                    "otherSameSecond": mode != 3,
                    "firstFlagAfter": first_flag,
                    "secondFlagAfter": second_flag,
                    "firstNeighbor": True, "secondNeighbor": False,
                    "firstSentinel": 17, "secondSentinel": 53,
                    "ownerSentinel": 97,
                })
    expected.append({
        "kind": "repeat", "afterFirst": -(1 << 31),
        "afterSecond": -(1 << 31) + 1,
        "firstFlag": True, "secondFlag": True,
        "currentSameSecond": True, "otherSameSecond": True,
        "firstNeighbor": True, "secondNeighbor": False,
        "firstSentinel": 17, "secondSentinel": 53,
        "ownerSentinel": 97,
    })
    return expected


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "call-before-capture-boolean-store", observations(), 5,
        "Five concrete methods; protected inherited receiver, call-before-null-failure effect order, Boolean store, "
        "alias identity, per-call receiver capture, Int32 wrap, and unchanged neighbors")
