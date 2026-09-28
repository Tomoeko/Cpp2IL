"""Independent finite oracle for Boolean tail calls through a receiver field."""

import json


def _read(kind, result, *, exception="none", owner_is_null=False,
          receiver_is_null=False, prefix=None, suffix=None):
    return {
        "kind": kind, "result": result, "exception": exception,
        "ownerIsNull": owner_is_null,
        "receiverIsNull": None if owner_is_null else receiver_is_null,
        "receiverSameWitness": None if owner_is_null else not receiver_is_null,
        "enabledAfter": True if kind != "false" else False,
        "neighborAfter": 23,
        "prefixAfter": prefix,
        "suffixAfter": suffix,
    }


def observations():
    return [
        {"kind": "constructors", "ownerCreated": True, "receiverCreated": True,
         "receiverIsNull": True, "enabled": False, "neighbor": 0,
         "prefix": 0, "suffix": 0},
        _read("false", False, prefix=-7, suffix=11),
        _read("true", True, prefix=-7, suffix=11),
        _read("shared", True, prefix=-13, suffix=17),
        _read("shared-original", True, prefix=-7, suffix=11),
        _read("null-receiver", None, exception="System.NullReferenceException",
              receiver_is_null=True, prefix=-19, suffix=29),
        _read("null-owner", None, exception="System.NullReferenceException",
              owner_is_null=True),
        {"kind": "folded-target-control", "result": True,
         "enabled": True, "neighbor": 31},
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "boolean-tail-field-call" or
            report.get("platform") != platform):
        raise ValueError("Boolean tail field-call report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Boolean tail field-call behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 6,
            "platform": report["platform"], "profile": "boolean-tail-field-call",
            "scope": "Boolean zero-argument tail call through a reference field, folded target control, both Boolean values, shared receiver, unchanged neighbors and both null paths"}
