"""Independent finite oracle for ordered reference-array field accesses."""

import json
from dataclasses import dataclass
from typing import Optional, Tuple


MINIMUM = -(1 << 31)
MAXIMUM = (1 << 31) - 1


@dataclass(frozen=True)
class Scenario:
    label: str
    initial: Optional[Tuple[int, ...]]
    replacement: Optional[Tuple[int, ...]]
    share_replacement: bool = False
    missing_owner: bool = False
    missing_holder: bool = False
    counter: int = 23
    marker: int = 47


SCENARIOS = (
    Scenario("null-owner", None, None, missing_owner=True),
    Scenario("null-holder", None, None, missing_holder=True),
    Scenario("null-array", None, (0,)),
    Scenario("empty", (), (0,)),
    Scenario("single-shared", (0,), None, share_replacement=True),
    Scenario("single-distinct", (0,), (1,)),
    Scenario("pair-swapped", (0, 1), (1, 0)),
    Scenario("repeated-elements", (0, 0, 2), (2, 0, 0)),
    Scenario("null-elements", (-1, 0, -1), (0, -1, -1)),
    Scenario("replacement-null", (0,), None),
    Scenario("replacement-shorter", (0, 1, 2), (2,)),
    Scenario("counter-marker-wrap", (0,), (1,), counter=MAXIMUM, marker=MAXIMUM),
)


def index_cases(first_length, second_length):
    return (
        ("same-index", 0, 0),
        ("first-to-second", 0, 1),
        ("last-to-first", first_length - 1, 0),
        ("first-to-last", 0, second_length - 1),
        ("negative-first", -1, 0),
        ("negative-second", 0, -1),
        ("upper-first", first_length, 0),
        ("upper-second", 0, second_length),
        ("both-negative", -1, -1),
        ("minimum-first", MINIMUM, MAXIMUM),
        ("maximum-second", 0, MAXIMUM),
    )


def int32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def expected_case(scenario, method, kind, first_index, second_index):
    has_reader = not scenario.missing_owner
    has_holder = has_reader and not scenario.missing_holder
    original = list(scenario.initial) if has_holder and scenario.initial is not None else None
    replacement = (original if scenario.share_replacement else
                   list(scenario.replacement) if has_holder and scenario.replacement is not None
                   else None)
    field = original
    counter = scenario.counter if has_holder else None
    marker = scenario.marker if has_reader else None
    result = None

    if not has_holder or original is None:
        exception = "System.NullReferenceException"
    elif first_index < 0 or first_index >= len(original):
        exception = "System.IndexOutOfRangeException"
    else:
        first_value = original[first_index]
        counter = int32(counter + 1)
        marker = int32(marker + 1)
        if method == "replacement":
            field = replacement
        if field is None:
            exception = "System.NullReferenceException"
        elif second_index < 0 or second_index >= len(field):
            exception = "System.IndexOutOfRangeException"
        else:
            exception = "none"
            result = first_value == field[second_index]

    return {
        "method": method, "kind": kind, "case": scenario.label,
        "firstIndex": first_index, "secondIndex": second_index,
        "result": result, "exception": exception,
        "originalBefore": None if original is None else original.copy(),
        "originalAfter": None if original is None else original.copy(),
        "replacementBefore": None if replacement is None else replacement.copy(),
        "replacementAfter": None if replacement is None else replacement.copy(),
        "counterBefore": scenario.counter if has_holder else None,
        "counterAfter": counter,
        "markerBefore": scenario.marker if has_reader else None,
        "markerAfter": marker,
        "sameFieldAsOriginal": has_holder and field is original,
        "sameFieldAsReplacement": has_holder and field is replacement,
    }


def observations():
    expected = []
    for scenario in SCENARIOS:
        first_length = len(scenario.initial) if scenario.initial is not None else 0
        replacement_length = (first_length if scenario.share_replacement else
                              len(scenario.replacement) if scenario.replacement is not None else 0)
        for method in ("twice", "replacement"):
            second_length = first_length if method == "twice" else replacement_length
            for kind, first, second in index_cases(first_length, second_length):
                expected.append(expected_case(scenario, method, kind, first, second))
    return expected


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
            report.get("stage") != stage or report.get("profile") != "composed-array" or \
            report.get("platform") != platform:
        raise ValueError("Composed-array report has the wrong version, stage, profile or platform")
    expected = observations()
    if not same_typed_value(report.get("observations"), expected):
        raise ValueError("Composed-array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "composed-array",
            "scope": "ordered reference-array reads, intervening effects and replacement, "
                     "reference equality, counter wrap, null and bounds failures"}
