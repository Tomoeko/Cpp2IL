"""Independent observations for an ordered effect and guarded no-argument tail call."""

import json


def observations():
    return [
        {"kind": "success", "failure": "none", "markCount": 1,
         "overrideCount": 0, "producerCount": 1,
         "nodeWasNull": False, "beforeApplied": 0, "afterApplied": 1,
         "hiddenCalls": None},
        {"kind": "missing-node", "failure": "System.NullReferenceException",
         "markCount": 1, "overrideCount": 0, "producerCount": 1,
         "nodeWasNull": True, "beforeApplied": 1, "afterApplied": 1,
         "hiddenCalls": None},
        {"kind": "missing-owner", "failure": "System.NullReferenceException",
         "markCount": None, "overrideCount": None, "producerCount": None,
         "nodeWasNull": None, "beforeApplied": 1, "afterApplied": 1,
         "hiddenCalls": None},
        {"kind": "hidden-method", "failure": "none", "markCount": 1,
         "overrideCount": 0, "producerCount": 1,
         "nodeWasNull": False, "beforeApplied": 0, "afterApplied": 1,
         "hiddenCalls": 0},
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}[stage]
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("platform") != expected_platform or
            report.get("profile") != "ordered-noarg-tail-guard" or
            json.dumps(report.get("observations"), sort_keys=True) !=
            json.dumps(observations(), sort_keys=True)):
        raise ValueError("Ordered no-argument tail guard behavior differs from the oracle")
    return {"status": "passed", "observations": len(observations()),
            "methods": 9, "platform": report["platform"],
            "profile": "ordered-noarg-tail-guard",
            "scope": "base virtual effect before guarded nonvirtual no-argument tail invocation"}
