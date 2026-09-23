"""Independent finite oracle for loop-carried sums and direct-call field effects."""

import json

VALUES = (-(1 << 31), -2, 0, 3, (1 << 31) - 1)
CALL_COUNTS = (-1, 0, (1 << 31) - 1)
COUNTS = (-3, 0, 1, 2, 7, 16)
DELTAS = (-(1 << 31), -1, 0, 1, (1 << 31) - 1)
SEEDS = (-(1 << 31), -7, 0, 19, (1 << 31) - 1)


def signed32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def loop_result(value, calls, iterations, seed=0):
    """Closed forms for sum(value + triangular(index)), final value and call count."""
    return {
        "result": signed32(seed + iterations * value + (iterations - 1) * iterations * (iterations + 1) // 6),
        "valueAfter": signed32(value + iterations * (iterations - 1) // 2),
        "callsAfter": signed32(calls + iterations),
    }


def observations():
    expected = [{"kind": "constructor", "valueAfter": 0, "callsAfter": 0}]
    for value in VALUES:
        for calls in CALL_COUNTS:
            for delta in DELTAS:
                result = signed32(value + delta)
                expected.append({"kind": "step", "initialValue": value, "initialCalls": calls, "delta": delta,
                                 "result": result, "valueAfter": result, "callsAfter": signed32(calls + 1)})
    for value in VALUES:
        for calls in CALL_COUNTS:
            for count in COUNTS:
                for seed in SEEDS:
                    expected.append({"kind": "run", "initialValue": value, "initialCalls": calls,
                                     "count": count, "seed": seed, **loop_result(value, calls, max(0, count), seed)})
    for value in VALUES:
        for calls in CALL_COUNTS:
            for count in COUNTS:
                for stop in (signed32(value - 1), value, signed32(value + 1), signed32(value + 6), 0):
                    maximum = max(0, count)
                    iterations = next((index + 1 for index in range(maximum)
                                       if signed32(value + index * (index + 1) // 2) == stop), maximum)
                    expected.append({"kind": "until", "initialValue": value, "initialCalls": calls,
                                     "count": count, "stop": stop, **loop_result(value, calls, iterations)})
    expected.append({"kind": "null", "member": "step", "argument": 1,
                     "result": None, "exception": "System.NullReferenceException"})
    for count in (-3, 0, 1):
        for member in ("run", "until"):
            expected.append({"kind": "null", "member": member, "argument": count,
                             "result": None if count > 0 else 19 if member == "run" else 0,
                             "exception": "System.NullReferenceException" if count > 0 else "none"})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "loop-calls":
        raise ValueError("Loop-call report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Loop-call native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Loop-call behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 4,
            "platform": report["platform"], "profile": "loop-calls",
            "scope": "finite loops, direct managed calls, field effects and null receivers; not whole-program equivalence"}
