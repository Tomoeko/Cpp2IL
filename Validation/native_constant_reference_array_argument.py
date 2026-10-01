"""Independent oracle for a constant first-element argument and terminal call."""

from behavior_oracle import verify_report


PROFILE = "native-constant-reference-array-argument"
ASSEMBLY = "NativeConstantReferenceArrayArgumentFixture"
DIRECT_CASES = ("direct-null-cell", "direct-first", "direct-second", "direct-null-payload",
                "direct-counter-negative", "direct-counter-overflow")
FORWARD_CASES = ("cell-null", "array-null", "array-empty", "first-null", "first-first",
                 "first-second", "element-alias", "value-alias", "counter-negative", "counter-overflow")
NULL_FAILURE = "System.NullReferenceException"
BOUNDS_FAILURE = "System.IndexOutOfRangeException"


def _values(values):
    return None if values is None else values.copy()


def _state(cells, arrays):
    def cell_state(cell):
        return {"items": "other" if cell["items"] == "empty" else cell["items"],
                "calls": cell["calls"], "value": cell["value"], "itemValues": _values(arrays[cell["items"]])}
    return {"first": cell_state(cells["first"]), "second": cell_state(cells["second"]),
            "firstArray": _values(arrays["first"]), "secondArray": _values(arrays["second"])}


def _record(kind, receiver, cells, arrays, direct=False, incoming="null"):
    before = _state(cells, arrays)
    failure = "none"
    if receiver == "null":
        failure = NULL_FAILURE
    else:
        cell = cells[receiver]
        items = arrays[cell["items"]]
        if not direct and items is None:
            failure = NULL_FAILURE
        elif not direct and not items:
            failure = BOUNDS_FAILURE
        else:
            cell["calls"] = ((cell["calls"] + 1 + (1 << 31)) & 0xffffffff) - (1 << 31)
            cell["value"] = incoming if direct else items[0]
    return {"kind": kind, "operation": "capture" if direct else "forward", "receiver": receiver,
            "argument": incoming if direct else "not-used", "exception": failure,
            "before": before, "after": _state(cells, arrays)}


def _fresh():
    cells = {"first": {"items": "first", "calls": 7, "value": "second"},
             "second": {"items": "second", "calls": -13, "value": "first"}}
    arrays = {"first": ["first", "second"], "second": ["second", "first"], "null": None, "empty": []}
    return cells, arrays


def observations():
    prefix = ASSEMBLY + "."
    rows = [{"kind": "declarations", "methods": 4, "fields": 3, "properties": 0, "types": 2,
             "signatures": {"Cell.Capture": ["System.Void", prefix + "Payload"], "Cell.Forward": ["System.Void"]},
             "fieldTypes": {"Cell.Items": prefix + "Payload[]", "Cell.Calls": "System.Int32", "Cell.Value": prefix + "Payload"},
             "fieldAccess": {"Cell.Items": "Public", "Cell.Calls": "Public", "Cell.Value": "Public"}},
            {"kind": "defaults", "itemsNull": True, "calls": 0, "valueNull": True}]
    for kind in DIRECT_CASES:
        cells, arrays = _fresh()
        receiver = "null" if kind == "direct-null-cell" else "second" if kind == "direct-second" else "first"
        incoming = "null" if kind == "direct-null-payload" else "second" if kind == "direct-second" else "first"
        if kind == "direct-counter-negative": cells["first"]["calls"] = -(1 << 31)
        if kind == "direct-counter-overflow": cells["first"]["calls"] = (1 << 31) - 1
        rows.append(_record(kind, receiver, cells, arrays, True, incoming))
    for kind in FORWARD_CASES:
        cells, arrays = _fresh()
        if kind == "array-null":
            arrays["first"] = None
            cells["first"]["items"] = "null"
        elif kind == "array-empty": arrays["first"] = []
        elif kind == "first-null": arrays["first"][0] = "null"
        elif kind == "first-second": arrays["first"] = ["second", "first"]
        elif kind == "element-alias": arrays["first"] = ["first", "first"]
        elif kind == "value-alias": cells["first"]["value"] = "first"
        elif kind == "counter-negative": cells["first"]["calls"] = -(1 << 31)
        elif kind == "counter-overflow": cells["first"]["calls"] = (1 << 31) - 1
        rows.append(_record(kind, "null" if kind == "cell-null" else "first", cells, arrays))
    cells, arrays = _fresh()
    cells["first"].update(items="null", calls=17)
    arrays["first"] = ["null", "second"]
    arrays["second"] = ["first", "second"]
    rows.append(_record("reuse-array-null", "first", cells, arrays))
    cells["first"]["items"] = "empty"
    rows.append(_record("reuse-array-empty", "first", cells, arrays))
    cells["first"]["items"] = "first"
    rows.append(_record("reuse-first-null", "first", cells, arrays))
    arrays["first"][0] = "first"
    rows.append(_record("reuse-first", "first", cells, arrays))
    rows.append(_record("repeat-first", "first", cells, arrays))
    arrays["first"][0] = "second"
    rows.append(_record("reuse-replaced-element", "first", cells, arrays))
    cells["first"]["items"] = "second"
    rows.append(_record("reuse-replaced-array", "first", cells, arrays))
    rows.append(_record("shared-array-second-cell", "second", cells, arrays))
    arrays["second"][0] = "null"
    rows.append(_record("shared-array-null-first-cell", "first", cells, arrays))
    arrays["second"][0] = "second"
    rows.append(_record("shared-array-replaced-second-cell", "second", cells, arrays))
    cells["first"]["items"] = "null"
    rows.append(_record("reuse-cleared-array", "first", cells, arrays))
    rows.append(_record("other-cell-retained-array", "second", cells, arrays))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, PROFILE, observations(), 4,
                         "Four concrete methods; constant index-zero reference argument, receiver/array/bounds failures "
                         "before callee effects, nullable identity, aliases, fresh reads, Int32 wrap and unchanged neighbors")
