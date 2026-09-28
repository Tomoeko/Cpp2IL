"""Independent value-type initialization and unsigned wrapper behavior oracle."""

import json

from scalar_wrapper import NARROW_PAIRS, NARROW_VALUES, WIDE_PAIRS, WIDE_VALUES


def _signed_word(bits):
    return bits - 0x100000000 if bits & 0x80000000 else bits


def _seed_row(step, value):
    return {"kind": "seed", "step": step, "value": value}


def observations():
    rows = [_seed_row("narrow-first-call", 17),
            _seed_row("narrow-second-call", 17),
            _seed_row("wide-first-call", 0xffffffff80000000),
            _seed_row("wide-second-call", 0xffffffff80000000)]
    for value in NARROW_VALUES:
        rows.append({"kind": "narrow", "operation": "value", "value": value,
                     "text": str(value), "afterText": value})
    for value in WIDE_VALUES:
        rows.append({"kind": "wide", "operation": "value", "value": value,
                     "text": str(value), "afterText": value,
                     "hash": _signed_word(((value >> 32) ^ value) & 0xffffffff),
                     "afterHash": value})
    for kind, pairs in (("narrow", NARROW_PAIRS), ("wide", WIDE_PAIRS)):
        for left, right in pairs:
            rows.append({"kind": kind, "operation": "compare",
                         "left": left, "right": right,
                         "result": (left > right) - (left < right),
                         "leftAfter": left, "rightAfter": right})
    rows.extend((_seed_row("narrow-after-all-calls", 17),
                 _seed_row("wide-after-all-calls", 0xffffffff80000000)))
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("platform") != platform or
            report.get("profile") != "scalar-wrapper-cctor"):
        raise ValueError("Scalar-wrapper constructor report has the wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Scalar-wrapper static values, returns or receiver state differ")
    return {"status": "passed", "observations": len(expected),
            "methods": 7, "platform": platform, "profile": "scalar-wrapper-cctor",
            "scope": "Four- and eight-byte beforefieldinit self-wrapper static initialization, unsigned edges and unchanged scalar receivers"}
