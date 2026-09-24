"""Independent finite oracle for two parameter-origin reference-array reads."""

import json
from dataclasses import dataclass
from typing import Optional, Tuple


MINIMUM = -(1 << 31)
MAXIMUM = (1 << 31) - 1


@dataclass(frozen=True)
class Scenario:
    label: str
    left: Optional[Tuple[int, ...]]
    right: Optional[Tuple[int, ...]]
    share_array: bool = False
    marker: int = 47


SCENARIOS = (
    Scenario("left-null", None, (0, 1)),
    Scenario("right-null", (0, 1), None),
    Scenario("both-null", None, None),
    Scenario("left-empty", (), (0, 1)),
    Scenario("right-empty", (0, 1), ()),
    Scenario("both-empty", (), ()),
    Scenario("equal-distinct", (0, 1), (0, 1)),
    Scenario("unequal-distinct", (0, 1), (2, 0)),
    Scenario("same-array", (0, 1, -1), None, share_array=True),
    Scenario("null-elements", (-1, 0), (-1, 1)),
    Scenario("one-null", (-1, 0), (1, -1)),
    Scenario("left-longer", (0, 1), (0,)),
    Scenario("right-longer", (0,), (0, 1)),
    Scenario("marker-wrap", (0, 1), (0, 2), marker=MAXIMUM),
)


def index_cases(left_length, right_length):
    return (
        ("zero", 0),
        ("one", 1),
        ("last-left", left_length - 1),
        ("last-right", right_length - 1),
        ("upper-both", max(left_length, right_length)),
        ("negative", -1),
        ("minimum", MINIMUM),
        ("maximum", MAXIMUM),
    )


def int32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def expected_case(scenario, kind, index):
    left = None if scenario.left is None else list(scenario.left)
    right = left if scenario.share_array else (
        None if scenario.right is None else list(scenario.right))
    marker = scenario.marker
    result = None

    if left is None:
        exception = "System.NullReferenceException"
    elif index < 0 or index >= len(left):
        exception = "System.IndexOutOfRangeException"
    else:
        first = left[index]
        marker = int32(marker + 1)
        if right is None:
            exception = "System.NullReferenceException"
        elif index < 0 or index >= len(right):
            exception = "System.IndexOutOfRangeException"
        else:
            exception = "none"
            result = first == right[index]

    return {
        "case": scenario.label, "kind": kind, "index": index,
        "result": result, "exception": exception,
        "leftBefore": left, "leftAfter": left,
        "rightBefore": right, "rightAfter": right,
        "markerBefore": scenario.marker, "markerAfter": marker,
        "sameArrays": scenario.share_array and left is not None,
    }


def observations():
    return [expected_case(scenario, kind, index)
            for scenario in SCENARIOS
            for kind, index in index_cases(
                len(scenario.left) if scenario.left is not None else 0,
                len(scenario.left) if scenario.share_array else
                len(scenario.right) if scenario.right is not None else 0)]


def same_typed_value(actual, expected):
    if type(actual) is not type(expected):
        return False
    if isinstance(expected, dict):
        return actual.keys() == expected.keys() and all(
            same_typed_value(actual[key], value) for key, value in expected.items())
    if isinstance(expected, list):
        return len(actual) == len(expected) and all(
            same_typed_value(left, right) for left, right in zip(actual, expected))
    return actual == expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if platform is None or report.get("unityVersion") != version or \
            report.get("stage") != stage or report.get("profile") != "parameter-array" or \
            report.get("platform") != platform:
        raise ValueError("Parameter-array report has the wrong version, stage, profile or platform")
    expected = observations()
    if not same_typed_value(report.get("observations"), expected):
        raise ValueError("Parameter-array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": report["platform"], "profile": "parameter-array",
            "scope": "two parameter-origin reference reads, array identity, first/second null "
                     "and bounds failures, observable marker effect and signed wrap"}
