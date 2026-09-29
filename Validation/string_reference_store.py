"""Independent behavior oracle for a null-guarded string field store."""

import json


def _success(kind, before, value, same_before=False):
    return {
        "kind": kind, "exception": "none", "value": value,
        "textBefore": before, "textAfter": value,
        "textSameValue": True, "textSameBefore": same_before,
        "prefixSame": True, "suffixSame": True,
        "sentinelBefore": 73, "sentinelAfter": 73,
    }


def _null_owner(kind, value):
    return {
        "kind": kind, "exception": "System.NullReferenceException", "value": value,
        "textBefore": None, "textAfter": None,
        "textSameValue": None, "textSameBefore": None,
        "prefixSame": None, "suffixSame": None,
        "sentinelBefore": None, "sentinelAfter": None,
    }


def observations():
    return [
        _success("new-value", None, "aaaaa"),
        _success("overwrite", "old", "nnnn"),
        _success("clear", "old", None),
        _success("already-null", None, None, same_before=True),
        _success("empty", "old", ""),
        _success("unicode", "old", "café-雪"),
        _success("same-reference", "sss", "sss", same_before=True),
        _success("sequence-first", "seed", "fffff"),
        _success("sequence-second", "fffff", "ssssss"),
        _null_owner("null-owner-value", "zz"),
        _null_owner("null-owner-null", None),
        _null_owner("null-owner-empty", ""),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "string-reference-store"):
        raise ValueError("String-reference-store report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("String-reference-store native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True, ensure_ascii=False) != json.dumps(
            expected, sort_keys=True, ensure_ascii=False):
        raise ValueError("String-reference-store behavior differs from the independent oracle")
    return {
        "status": "passed", "observations": len(expected), "methods": 2,
        "platform": report["platform"], "profile": "string-reference-store",
        "scope": "direct string field store with null owner, overwrites, ordered writes, and unchanged neighbors",
    }
