"""Finite independent oracle for a class-return tail call through one reference field."""

import json


def _case(kind, before, after, prefix, suffix, *, exception="none",
          owner_is_null=False, field_is_null=False, field_same_witness=True,
          alias_length=None, alias_prefix=None, alias_suffix=None):
    has_owner = not owner_is_null
    has_alias = alias_prefix is not None
    success = exception == "none"
    return {
        "kind": kind, "exception": exception, "ownerIsNull": owner_is_null,
        "fieldIsNull": field_is_null if has_owner else None,
        "returnedSameWitness": True if success else None,
        "returnedSameField": True if success else None,
        "fieldSameBefore": True if has_owner else None,
        "fieldSameWitness": field_same_witness if has_owner else None,
        "lengthBefore": before, "lengthAfter": after,
        "prefixAfter": prefix, "suffixAfter": suffix,
        "aliasSameWitness": True if has_alias else None,
        "aliasLengthAfter": alias_length,
        "aliasPrefixAfter": alias_prefix,
        "aliasSuffixAfter": alias_suffix,
    }


def observations():
    return [
        {"kind": "fresh", "bufferIsNull": True, "prefix": 0, "suffix": 0},
        _case("first", 5, 0, -11, 13),
        _case("repeat", 5, 0, -11, 13),
        _case("alias-left", 6, 0, -17, 19,
              alias_length=0, alias_prefix=-23, alias_suffix=29),
        _case("alias-right", 6, 0, -23, 29,
              alias_length=0, alias_prefix=-17, alias_suffix=19),
        _case("null-field", 7, 7, -31, 31,
              exception="System.NullReferenceException", field_is_null=True,
              field_same_witness=False),
        _case("null-owner", 9, 9, None, None,
              exception="System.NullReferenceException", owner_is_null=True,
              alias_length=9, alias_prefix=-37, alias_suffix=37),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "reference-tail-call" or
            report.get("platform") != platform):
        raise ValueError("Reference tail-call report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Reference tail-call behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 2,
            "platform": report["platform"], "profile": "reference-tail-call",
            "scope": "class return identity, cleared state, aliased buffer, unchanged neighbors, and both null paths"}
