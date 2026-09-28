"""Independent observations for an ordered virtual base call and guarded tail call."""

import json


def observations():
    return [
        {"kind": "success", "failure": "none", "markCount": 1,
         "overrideCount": 0, "producerCount": 1,
         "nodeWasNull": False, "beforeFlag": False, "afterFlag": True,
         "hiddenCalls": None},
        {"kind": "missing-node", "failure": "System.NullReferenceException",
         "markCount": 1, "overrideCount": 0, "producerCount": 1,
         "nodeWasNull": True, "beforeFlag": True,
         "afterFlag": True, "hiddenCalls": None},
        {"kind": "missing-owner", "failure": "System.NullReferenceException",
         "markCount": None, "overrideCount": None, "producerCount": None,
         "nodeWasNull": None, "beforeFlag": True,
         "afterFlag": True, "hiddenCalls": None},
        {"kind": "hidden-method", "failure": "none", "markCount": 1,
         "overrideCount": 0, "producerCount": 1,
         "nodeWasNull": False, "beforeFlag": False, "afterFlag": True,
         "hiddenCalls": 0},
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    expected_platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}[stage]
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("platform") != expected_platform or
            report.get("profile") != "ordered-call-tail-guard" or
            json.dumps(report.get("observations"), sort_keys=True) !=
            json.dumps(observations(), sort_keys=True)):
        raise ValueError("Ordered-call tail guard behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(observations()),
            "methods": 9, "platform": report["platform"],
            "profile": "ordered-call-tail-guard",
            "scope": "base virtual call before guarded nonvirtual Boolean tail invocation"}
