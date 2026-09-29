"""Independent behavior oracle for a null-guarded object field store."""

import json


def _success(kind, before, value, same_before=False):
    return {
        "kind": kind, "exception": "none", "value": value,
        "itemBefore": before, "itemAfter": value,
        "itemSameValue": True, "itemSameBefore": same_before,
        "prefixSame": True, "suffixSame": True,
        "sentinelBefore": 61, "sentinelAfter": 61,
    }


def _null_owner(kind, value):
    return {
        "kind": kind, "exception": "System.NullReferenceException", "value": value,
        "itemBefore": "null", "itemAfter": "null",
        "itemSameValue": None, "itemSameBefore": None,
        "prefixSame": None, "suffixSame": None,
        "sentinelBefore": None, "sentinelAfter": None,
    }


def observations():
    return [
        _success("new-object", "null", "System.Object"),
        _success("overwrite-object", "string:old", "System.Object"),
        _success("store-string", "System.Object", "string:text-雪"),
        _success("store-boxed-int", "string:old", "int:23"),
        _success("clear", "System.Object", "null"),
        _success("already-null", "null", "null", same_before=True),
        _success("same-reference", "System.Object", "System.Object", same_before=True),
        _success("sequence-first", "string:seed", "int:17"),
        _success("sequence-second", "int:17", "string:last"),
        _null_owner("null-owner-object", "System.Object"),
        _null_owner("null-owner-null", "null"),
        _null_owner("null-owner-string", "string:text"),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "object-reference-store"):
        raise ValueError("Object-reference-store report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Object-reference-store native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True, ensure_ascii=False) != json.dumps(
            expected, sort_keys=True, ensure_ascii=False):
        raise ValueError("Object-reference-store behavior differs from the independent oracle")
    return {
        "status": "passed", "observations": len(expected), "methods": 2,
        "platform": report["platform"], "profile": "object-reference-store",
        "scope": "direct object field store with null owner, mixed value types, ordered writes, and unchanged neighbors",
    }
