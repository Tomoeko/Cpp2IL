"""Independent finite oracle for a parameter-origin reference class test."""

import json


def observations():
    compatible = {
        "direct": ("RecoveryValidation.BehaviorProbe+RemoteValue", "direct", 17),
        "subtype": ("RecoveryValidation.BehaviorProbe+RemoteChild", "subtype", 23),
        "direct-repeat": ("RecoveryValidation.BehaviorProbe+RemoteValue", "direct", 17),
        "other-direct": ("RecoveryValidation.BehaviorProbe+RemoteValue", "other direct", 31),
    }
    return [
        {"kind": kind, "sameReference": True,
         "resultType": compatible.get(kind, ("null", "null", 0))[0],
         "label": compatible.get(kind, ("null", "null", 0))[1],
         "marker": compatible.get(kind, ("null", "null", 0))[2], "failure": "none"}
        for kind in ("null", "direct", "subtype", "direct-repeat", "string",
                     "boxed-int", "array", "object", "other-direct")
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "parameter-class-test" or
            report.get("platform") != platform or
            report.get("observations") != observations()):
        raise ValueError("Parameter class-test behavior differs from the independent oracle")
    return {"status": "passed", "observations": 9, "methods": 1,
            "platform": platform, "profile": "parameter-class-test",
            "scope": "MarshalByRefObject class test over null, compatible, incompatible and boxed inputs"}
