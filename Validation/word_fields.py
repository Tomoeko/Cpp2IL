"""Independent exhaustive oracle for exact 16-bit field zero comparisons."""

import base64
import binascii
import json


PATTERN_COUNT = 1 << 16
FIELD_CASES = (
    ("IsShortZero", "SignedValue", "System.Int16"),
    ("IsUShortZero", "UnsignedValue", "System.UInt16"),
    ("IsCharZero", "CharacterValue", "System.Char"),
)


def _bitset(value, expected, description):
    if not isinstance(value, str):
        raise ValueError(description + " must be a base64 bitset")
    try:
        decoded = base64.b64decode(value, validate=True)
    except (ValueError, binascii.Error) as exception:
        raise ValueError(description + " has invalid base64") from exception
    if len(decoded) != PATTERN_COUNT // 8 or decoded != expected:
        raise ValueError(description + " differs from the exhaustive oracle")


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage
            or report.get("profile") != "word-fields"):
        raise ValueError("Word-field report has the wrong version, stage, or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Word-field behavior was not measured in a Windows player")
    if (type(report.get("patternCount")) is not int or report["patternCount"] != PATTERN_COUNT
            or report.get("bitEncoding") != "unsigned16-pattern-index-lsb-first"):
        raise ValueError("Word-field input denominator or bit encoding is invalid")

    expected_fields = [{"field": field, "managedType": managed_type, "value": 0}
                       for _, field, managed_type in FIELD_CASES]
    initial = report.get("initialFields")
    if (initial != expected_fields or not isinstance(initial, list)
            or any(type(row.get("value")) is not int for row in initial)):
        raise ValueError("Fresh word fields differ from their declared zero defaults")

    predicates = report.get("predicates")
    if not isinstance(predicates, list) or len(predicates) != len(FIELD_CASES):
        raise ValueError("Word-field predicate scope is incomplete")
    # Only the all-zero 16-bit pattern equals zero for short, ushort, or char.
    expected_zero = b"\x01" + bytes(PATTERN_COUNT // 8 - 1)
    expected_unchanged = b"\xff" * (PATTERN_COUNT // 8)
    for row, (member, field, managed_type) in zip(predicates, FIELD_CASES):
        if (row.get("member"), row.get("inputField"), row.get("inputType")) != (member, field, managed_type):
            raise ValueError("Word-field predicate identity differs")
        _bitset(row.get("zeroResults"), expected_zero, member + " results")
        unchanged = row.get("unchangedFields")
        if not isinstance(unchanged, dict) or set(unchanged) != {item[1] for item in FIELD_CASES}:
            raise ValueError("Word-field mutation coverage is incomplete")
        for _, checked_field, _ in FIELD_CASES:
            _bitset(unchanged[checked_field], expected_unchanged, member + " preserves " + checked_field)

    expected_nulls = [{"member": member, "exception": "System.NullReferenceException"}
                      for member, _, _ in FIELD_CASES]
    if report.get("nullReceivers") != expected_nulls:
        raise ValueError("Word-field null calls differ from managed null-receiver behavior")
    return {"status": "passed", "methods": 4, "inputPatternsPerPredicate": PATTERN_COUNT,
            "predicateObservations": len(FIELD_CASES) * PATTERN_COUNT,
            "fieldPreservationObservations": len(FIELD_CASES) ** 2 * PATTERN_COUNT,
            "freshFieldObservations": len(FIELD_CASES), "nullCallObservations": len(FIELD_CASES),
            "observations": len(FIELD_CASES) * PATTERN_COUNT + len(FIELD_CASES) ** 2 * PATTERN_COUNT + 6,
            "platform": report["platform"],
            "scope": "exhaustive16-bit field equality and field preservation; ordinary managed null calls"}
