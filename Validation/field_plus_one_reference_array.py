"""Typed oracle for a reference-array getter using an Int32 field plus one."""

from behavior_oracle import verify_report


POSITIONS = (-2, -1, 0, 1, 2, -(1 << 31), (1 << 31) - 1)
NULL_FAILURE = "System.NullReferenceException"
BOUNDS_FAILURE = "System.IndexOutOfRangeException"


def _row(kind, owner, position, length=None):
    owner_exists = kind != "owner-null"
    items_exist = owner_exists and kind != "array-null"
    selected = (position + 1) & 0xFFFFFFFF
    if selected >= 1 << 31:
        selected -= 1 << 32
    valid = items_exist and 0 <= selected < length
    failure = (NULL_FAILURE if not items_exist else
               "none" if valid else BOUNDS_FAILURE)
    null_element = kind == "element-null"
    return {
        "kind": kind, "owner": owner, "position": position,
        "resultId": None if not valid or null_element else selected + 11,
        "exception": failure,
        "sameElement": True if valid else None,
        "sameArray": True if owner_exists else None,
        "positionUnchanged": True if owner_exists else None,
        "before": -53 if owner_exists else None,
        "spacer": -61 if owner == "B" and owner_exists else None,
        "after": 59 if owner_exists else None,
        "itemsUnchanged": True if items_exist else None,
    }


def observations():
    rows = []
    for length in range(4):
        for position in POSITIONS:
            for owner in ("A", "B"):
                rows.append(_row(f"length-{length}", owner, position, length))
    for position in (-1, 0, (1 << 31) - 1):
        for owner in ("A", "B"):
            rows.append(_row("array-null", owner, position))
        for owner in ("A", "B"):
            rows.append(_row("owner-null", owner, position))
    for owner in ("A", "B"):
        rows.append(_row("element-null", owner, -1, 2))
    return rows


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "field-plus-one-reference-array",
        observations(), 5,
        "Two owner layouts with ordered array-null and bounds failures, signed "
        "field-plus-one indexing, reference identity, null elements, and unchanged fields")
