"""Independent first-match, alias and signed-key oracle for reference-array search."""

from behavior_oracle import verify_report


CASES = ("holder-null", "null-array", "empty", "all-null", "distinct",
         "duplicate", "alias", "negative", "extremes", "tail-match")
KEYS = (-(1 << 31), -1, 0, 1, (1 << 31) - 1)
TAG = "00000011-0013-0017-1d1f-25292b2f353b"
VALUES = {
    "holder-null": (0, 1), "null-array": None, "empty": (), "all-null": (None, None),
    "distinct": (0, 1, -1), "duplicate": (1, 1, 0), "alias": (1, -1, 1),
    "negative": (-1, KEYS[0], 0), "extremes": (KEYS[0], KEYS[-1], 0),
    "tail-match": (None, 0, None, 1),
}


def state(kind, padded):
    values = VALUES[kind]
    if values is None:
        return None
    return [None if value is None else {
        "identity": 0 if kind == "alias" and index == 2 else index,
        "key": value,
        **({"tag": TAG, "text": "item-" + str(0 if kind == "alias" and index == 2 else index)}
           if padded else {}),
    } for index, value in enumerate(values)]


def observations():
    prefix = "ReferenceArraySearchFixture."
    rows = [{"kind": "declarations", "methods": 8, "fields": 7,
             "simpleArrayType": prefix + "Entry[]", "paddedArrayType": prefix + "PaddedEntry[]",
             "keyType": "System.Int32", "paddedKeyType": "System.Int32", "tagType": "System.Guid",
             "textType": "System.String", "signatures": {
                 owner + "." + method: [result, "System.Int32"]
                 for owner in ("SearchHolder", "PaddedSearchHolder")
                 for method, result in (("Find", "System.Int32"), ("Contains", "System.Boolean"))}},
            {"kind": "defaults", "key": 0, "paddedKey": 0,
             "tag": "00000000-0000-0000-0000-000000000000", "text": None,
             "simpleItemsNull": True, "paddedItemsNull": True, "neighbor": 0}]
    for padded in (False, True):
        for operation in ("find", "contains"):
            for kind in CASES:
                for key in KEYS:
                    values = VALUES[kind]
                    found = next((index for index, value in enumerate(values or ())
                                  if value is not None and value == key), -1)
                    result = found if operation == "find" else found >= 0
                    if kind == "holder-null":
                        result = None
                    rows.append({"kind": kind, "layout": "padded" if padded else "simple",
                                 "operation": operation, "key": key, "result": result,
                                 "repeatedResult": result,
                                 "exception": "System.NullReferenceException" if kind == "holder-null" else "none",
                                 "itemsBefore": state(kind, padded), "itemsAfter": state(kind, padded),
                                 "neighbor": 43})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "reference-array-search", observations(), 8,
                         "Two ordinary reference layouts: captured array first-match search, null-array absence, "
                         "null-element skips, duplicate/alias keys, signed boundaries and repeat preservation")
