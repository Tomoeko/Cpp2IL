"""Independent finite oracle for a guarded direct tail invocation."""

import json


def _call(kind, before, after, producer, *, exception="none",
          owner_is_null=False, node_is_null=False, shadow_calls=None):
    return {
        "kind": kind,
        "exception": exception,
        "ownerIsNull": owner_is_null,
        "nodeIsNull": None if owner_is_null else node_is_null,
        "nodeSameWitness": None if owner_is_null else not node_is_null,
        "producerCount": producer,
        "lastValueBefore": before,
        "lastValueAfter": after,
        "shadowCalls": shadow_calls,
    }


def observations():
    return [
        {"kind": "constructors", "ownerCreated": True, "nodeCreated": True,
         "nodeIsNull": True, "producerCount": 0, "lastValue": 0},
        _call("success", -11, 7, 1),
        _call("missing-node", 7, 7, 1,
              exception="System.NullReferenceException", node_is_null=True),
        _call("missing-owner", 7, 7, None,
              exception="System.NullReferenceException", owner_is_null=True),
        _call("shared-first", 7, 7, 1),
        _call("shared-second", 7, 7, 1),
        _call("derived-node", -19, 7, 1, shadow_calls=0),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "call-result-tail-guard" or
            report.get("platform") != platform):
        raise ValueError("Call-result tail guard report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Call-result tail guard behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 5,
            "platform": report["platform"], "profile": "call-result-tail-guard",
            "scope": "Producer side effect precedes direct tail target, including null result and nonvirtual derived receiver"}
