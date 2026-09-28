"""Independent observations for a Boolean-false guarded call-result tail."""

import json


def _call(kind, before_value, after_value, before_count, after_count,
          producer_count, *, exception="none", owner_is_null=False,
          node_is_null=False, hidden_calls=None, override_calls=None):
    return {
        "kind": kind,
        "exception": exception,
        "ownerIsNull": owner_is_null,
        "nodeIsNull": None if owner_is_null else node_is_null,
        "nodeSameWitness": None if owner_is_null else not node_is_null,
        "producerCount": producer_count,
        "lastValueBefore": before_value,
        "lastValueAfter": after_value,
        "applyCountBefore": before_count,
        "applyCountAfter": after_count,
        "hiddenCalls": hidden_calls,
        "overrideCalls": override_calls,
    }


def observations():
    return [
        {"kind": "constructors", "ownerCreated": True, "nodeCreated": True,
         "nodeIsNull": True, "producerCount": 0,
         "lastValue": False, "applyCount": 0},
        _call("ordinary-success", True, False, 0, 1, 1),
        _call("ordinary-missing-node", False, False, 1, 1, 1,
              exception="System.NullReferenceException", node_is_null=True),
        _call("ordinary-missing-owner", False, False, 1, 1, None,
              exception="System.NullReferenceException", owner_is_null=True),
        _call("shared-first", False, False, 1, 2, 1),
        _call("shared-second", False, False, 2, 3, 1),
        _call("hidden-node", True, False, 0, 1, 1, hidden_calls=0),
        _call("virtual-success", True, False, 0, 1, 1),
        _call("virtual-missing-node", False, False, 1, 1, 1,
              exception="System.NullReferenceException", node_is_null=True),
        _call("virtual-override", True, True, 0, 0, 0, override_calls=1),
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "call-result-false-tail" or
            report.get("platform") != platform):
        raise ValueError("Boolean-false tail report has the wrong version, stage, profile or platform")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Boolean-false tail behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 7,
            "platform": report["platform"], "profile": "call-result-false-tail",
            "scope": "effectful producer before a false Boolean tail call and its null failure"}
