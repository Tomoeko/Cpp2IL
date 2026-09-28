"""Independent unsigned scalar-wrapper behavior oracle."""

import json


NARROW_VALUES = (0, 1, 0x7fffffff, 0x80000000, 0xffffffff)
WIDE_VALUES = (0, 1, 0x100000000, 0x7fffffffffffffff,
               0x8000000000000000, 0xffffffffffffffff)
NARROW_PAIRS = ((0, 0), (0, 1), (1, 0),
                (0x7fffffff, 0x80000000),
                (0x80000000, 0x7fffffff),
                (0xffffffff, 0), (0, 0xffffffff),
                (0xffffffff, 0xffffffff))
WIDE_PAIRS = ((0, 0), (0, 1), (1, 0),
              (0x7fffffffffffffff, 0x8000000000000000),
              (0x8000000000000000, 0x7fffffffffffffff),
              (0xffffffffffffffff, 0),
              (0, 0xffffffffffffffff),
              (0xffffffffffffffff, 0xffffffffffffffff),
              (0x100000000, 0xffffffff))


def _signed_word(bits):
    return bits - 0x100000000 if bits & 0x80000000 else bits


def _hash_wide(value):
    return _signed_word(((value >> 32) ^ value) & 0xffffffff)


def _value_row(kind, value, hash_code=None):
    row = {"kind": kind, "operation": "value", "value": value,
           "text": str(value), "afterText": value}
    if hash_code is not None:
        row.update(hash=hash_code, afterHash=value)
    return row


def _compare_row(kind, left, right):
    result = (left > right) - (left < right)
    return {"kind": kind, "operation": "compare",
            "left": left, "right": right, "result": result,
            "leftAfter": left, "rightAfter": right}


def observations():
    rows = []
    for value in NARROW_VALUES:
        rows.append(_value_row("narrow-a", value))
        rows.append(_value_row("narrow-b", value))
    for value in WIDE_VALUES:
        rows.append(_value_row("wide", value, _hash_wide(value)))
    for left, right in NARROW_PAIRS:
        rows.append(_compare_row("narrow-a", left, right))
        rows.append(_compare_row("narrow-b", left, right))
    for left, right in WIDE_PAIRS:
        rows.append(_compare_row("wide", left, right))
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("platform") != platform or
            report.get("profile") != "scalar-wrapper"):
        raise ValueError("Scalar-wrapper report has the wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Scalar-wrapper values, hashes, comparisons or receiver state differ")
    return {"status": "passed", "observations": len(expected),
            "methods": 7, "platform": platform, "profile": "scalar-wrapper",
            "scope": "Two same-width wrapper identities and a wider unsigned wrapper: decimal returns, wide hash, unsigned comparisons and unchanged receiver and argument values"}
