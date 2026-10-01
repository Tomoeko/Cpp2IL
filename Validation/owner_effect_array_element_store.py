"""Independent oracle for an owner effect before a checked element store."""

from behavior_oracle import verify_report


PROFILE = "owner-effect-array-element-store"
ASSEMBLY = "OwnerEffectArrayElementStoreFixture"
INDICES = (-(1 << 31), -1, 0, 1, 2, 3, (1 << 31) - 1)
CASES = ("aliased", "element-null", "element-null-negative", "array-null", "array-null-minimum", "owner-null", "array-empty")
NULL_FAILURE = "System.NullReferenceException"
BOUNDS_FAILURE = "System.IndexOutOfRangeException"


def _fresh(active):
    owners = {"first": {"before": 43, "active": active, "after": 127, "items": "first"},
              "second": {"before": 59, "active": not active, "after": 139, "items": "second"}}
    cells = {"first": {"before": 17, "enabled": True, "after": 83},
             "second": {"before": 29, "enabled": False, "after": 101},
             "third": {"before": 41, "enabled": True, "after": 113}}
    arrays = {"first": ["first", "second", "third"], "second": ["third", "first", "second"], "null": None}
    return owners, cells, arrays


def _values(items):
    return None if items is None else items.copy()


def _state(owners, cells, arrays):
    def owner_state(owner):
        return {**owner, "itemValues": _values(arrays[owner["items"]])}
    return {"first": owner_state(owners["first"]), "second": owner_state(owners["second"]),
            "firstCell": cells["first"].copy(), "secondCell": cells["second"].copy(), "thirdCell": cells["third"].copy(),
            "firstArray": _values(arrays["first"]), "secondArray": _values(arrays["second"])}


def _record(kind, receiver, index, owners, cells, arrays):
    before = _state(owners, cells, arrays)
    failure = "none"
    if receiver == "null":
        failure = NULL_FAILURE
    else:
        owner = owners[receiver]
        owner["active"] = False
        items = arrays[owner["items"]]
        if items is None:
            failure = NULL_FAILURE
        elif index < 0 or index >= len(items):
            failure = BOUNDS_FAILURE
        elif items[index] == "null":
            failure = NULL_FAILURE
        else:
            cells[items[index]]["enabled"] = False
    return {"kind": kind, "receiver": receiver, "index": index, "exception": failure,
            "before": before, "after": _state(owners, cells, arrays)}


def observations():
    prefix = ASSEMBLY + "."
    field_types = {"Cell.Before": "System.Byte", "Cell.Enabled": "System.Boolean", "Cell.After": "System.Byte",
                   "Catalog.Before": "System.Int32", "Catalog.Active": "System.Boolean", "Catalog.After": "System.Byte",
                   "Catalog.Items": prefix + "Cell[]"}
    rows = [{"kind": "declarations", "methods": 3, "fields": 7, "properties": 0, "types": 2,
             "signatures": {"Catalog.Clear": ["System.Void", "System.Int32"]}, "fieldTypes": field_types,
             "fieldAccess": {name: "Public" for name in field_types}, "sealedTypes": [True, True]},
            {"kind": "defaults", "cell": {"before": 0, "enabled": False, "after": 0},
             "catalog": {"before": 0, "active": False, "after": 0, "items": "null", "itemValues": None}}]
    for active in (False, True):
        for index in INDICES:
            owners, cells, arrays = _fresh(active)
            rows.append(_record("length-three", "first", index, owners, cells, arrays))
        for kind in CASES:
            owners, cells, arrays = _fresh(active)
            index = 1 if kind == "element-null" else -1 if kind == "element-null-negative" else -(1 << 31) if kind == "array-null-minimum" else 0
            if kind in ("array-null", "array-null-minimum"):
                arrays["first"] = None
                owners["first"]["items"] = "null"
            elif kind == "array-empty": arrays["first"] = []
            elif kind == "aliased": arrays["first"][1] = "first"
            elif kind in ("element-null", "element-null-negative"): arrays["first"][1] = "null"
            rows.append(_record(kind, "null" if kind == "owner-null" else "first", index, owners, cells, arrays))
    owners, cells, arrays = _fresh(True)
    arrays["second"] = ["second", "first", "third"]
    rows.append(_record("reuse-first", "first", 0, owners, cells, arrays))
    arrays["first"][0] = "null"
    owners["first"]["active"] = True
    rows.append(_record("reuse-null-element", "first", 0, owners, cells, arrays))
    arrays["first"][0] = "first"
    cells["first"]["enabled"] = cells["second"]["enabled"] = True
    owners["first"].update(items="second", active=True)
    rows.append(_record("reuse-replaced-array", "first", 0, owners, cells, arrays))
    owners["first"]["active"] = owners["second"]["active"] = True
    rows.append(_record("shared-other-owner", "second", 1, owners, cells, arrays))
    owners["first"].update(items="null", active=True)
    rows.append(_record("reuse-null-array", "first", 0, owners, cells, arrays))
    owners["first"].update(items="second", active=True)
    rows.append(_record("reuse-negative-bound", "first", -1, owners, cells, arrays))
    arrays["second"][1] = "third"
    owners["second"]["active"] = True
    rows.append(_record("shared-replaced-element", "second", 1, owners, cells, arrays))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, PROFILE, observations(), 3,
                         "Three concrete methods; owner Boolean clear precedes checked array/element failures, "
                         "unsigned bounds semantics, Boolean element stores, aliases, array replacement and unchanged byte/Int32 neighbors")
