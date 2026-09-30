"""Independent signed three-way comparison and immutable field-state oracle."""

from behavior_oracle import verify_report

CASES = ("distinct", "alias", "first-null", "second-null", "both-null", "comparer-null")
PAIRS = ((-(1 << 31), (1 << 31) - 1), ((1 << 31) - 1, -(1 << 31)),
         (-(1 << 31), -(1 << 31)), ((1 << 31) - 1, (1 << 31) - 1),
         (-1, 0), (0, -1), (0, 0), (1, 0), (0, 1))
OPERATIONS = ("compare", "compareAgain", "comparePadded")
TAG = "00000011-0013-0017-1d1f-25292b2f353b"
PADDING = [11, 13, 17, 19, 23, 29, 31]


def state(key, identity, padded):
    return {"identity": identity, "key": key,
            **({"tag": TAG, "padding": PADDING[:]} if padded else {"neighbor": 43})}


def observations():
    prefix = "SignedFieldComparisonFixture."
    rows = [{"kind": "declarations", "methods": 6, "fields": 11, "keyType": "System.Int32",
             "paddedKeyType": "System.Int32", "tagType": "System.Guid", "signatures": {
                 name: ["System.Int32", prefix + item, prefix + item]
                 for name, item in (("Compare", "ComparisonItem"), ("CompareAgain", "ComparisonItem"),
                                    ("ComparePadded", "PaddedComparisonItem"))}},
            {"kind": "defaults", "key": 0, "neighbor": 0, "paddedKey": 0,
             "tag": "00000000-0000-0000-0000-000000000000", "padding": [0] * 7}]
    for operation in OPERATIONS:
        for kind in CASES:
            for first_key, second_key in PAIRS:
                padded = operation == "comparePadded"
                first = None if kind in ("first-null", "both-null") else state(first_key, "first", padded)
                second = None if kind in ("second-null", "both-null") else state(
                    first_key if kind == "alias" else second_key, "first" if kind == "alias" else "second", padded)
                throws = first is None or second is None or kind == "comparer-null"
                result = None if throws else (first["key"] > second["key"]) - (first["key"] < second["key"])
                rows.append({"kind": kind, "operation": operation, "firstKey": first_key, "secondKey": second_key,
                             "result": result, "repeatedResult": result,
                             "exception": "System.NullReferenceException" if throws else "none",
                             "firstBefore": first, "secondBefore": second, "firstAfter": first, "secondAfter": second})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "signed-field-comparison", observations(), 6,
                         "Signed three-way field comparisons with two read rounds, aliases, null operands, "
                         "padded layout, signed boundaries and unchanged repeated-call state")
