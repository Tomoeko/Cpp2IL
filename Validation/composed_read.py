"""Independent finite oracle for ordered reads of a nested reference array."""

import json
from dataclasses import dataclass
from typing import Optional, Tuple


MINIMUM = -(1 << 31)
MAXIMUM = (1 << 31) - 1


@dataclass(frozen=True)
class Scenario:
    label: str
    items: Optional[Tuple[int, ...]]
    missing_owner: bool = False
    missing_holder: bool = False
    counter: int = 23
    marker: int = 47


SCENARIOS = (
    Scenario("null-owner", None, missing_owner=True),
    Scenario("null-holder", None, missing_holder=True),
    Scenario("null-array", None),
    Scenario("empty", ()),
    Scenario("single", (0,)),
    Scenario("distinct-elements", (0, 1)),
    Scenario("repeated-elements", (0, 0, 2)),
    Scenario("null-elements", (-1, 0, -1)),
    Scenario("counter-marker-wrap", (0, 1), counter=MAXIMUM, marker=MAXIMUM),
)


def index_cases(length):
    return (
        ("same-index", 0, 0),
        ("first-to-second", 0, 1),
        ("last-to-first", length - 1, 0),
        ("first-to-last", 0, length - 1),
        ("negative-first", -1, 0),
        ("negative-second", 0, -1),
        ("upper-first", length, 0),
        ("upper-second", 0, length),
        ("both-negative", -1, -1),
        ("minimum-first", MINIMUM, MAXIMUM),
        ("maximum-second", 0, MAXIMUM),
    )


def int32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def expected_case(scenario, kind, first_index, second_index):
    has_reader = not scenario.missing_owner
    has_holder = has_reader and not scenario.missing_holder
    items = list(scenario.items) if has_holder and scenario.items is not None else None
    counter = scenario.counter if has_holder else None
    marker = scenario.marker if has_reader else None
    result = None

    if not has_holder or items is None:
        exception = "System.NullReferenceException"
    elif first_index < 0 or first_index >= len(items):
        exception = "System.IndexOutOfRangeException"
    else:
        first_value = items[first_index]
        counter = int32(counter + 1)
        marker = int32(marker + 1)
        if second_index < 0 or second_index >= len(items):
            exception = "System.IndexOutOfRangeException"
        else:
            exception = "none"
            result = first_value == items[second_index]

    return {
        "case": scenario.label, "kind": kind,
        "firstIndex": first_index, "secondIndex": second_index,
        "result": result, "exception": exception,
        "itemsBefore": None if items is None else items.copy(),
        "itemsAfter": None if items is None else items.copy(),
        "counterBefore": scenario.counter if has_holder else None,
        "counterAfter": counter,
        "markerBefore": scenario.marker if has_reader else None,
        "markerAfter": marker,
        "sameItemsAsOriginal": has_holder,
        "sameHolderAsOriginal": has_reader,
    }


def observations():
    return [expected_case(scenario, kind, first, second)
            for scenario in SCENARIOS
            for kind, first, second in index_cases(
                len(scenario.items) if scenario.items is not None else 0)]


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
            report.get("stage") != stage or report.get("profile") != "composed-read" or \
            report.get("platform") != platform:
        raise ValueError("Composed-read report has the wrong version, stage, profile or platform")
    expected = observations()
    if not same_typed_value(report.get("observations"), expected):
        raise ValueError("Composed-read behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": report["platform"], "profile": "composed-read",
            "scope": "reference-array identity, preserved effects, signed wrap, "
                     "first-access null and both bounds-failure positions"}
