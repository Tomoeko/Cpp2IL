"""Independent finite oracle for a Boolean parameter-origin reference class test."""

import json


def observations():
    compatible = {"direct", "subtype", "direct-repeat"}
    return [
        {"kind": kind, "result": kind in compatible, "failure": "none",
         "hresult": 0, "formatCalls": 0}
        for kind in ("null-cold", "object", "string", "boxed-int", "object-vector",
                     "multidimensional", "string-vector", "direct", "subtype",
                     "direct-repeat", "object-repeat")
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    expected = observations()
    actual = report.get("observations")
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "boolean-parameter-class-test" or
            report.get("platform") != platform or actual != expected or
            any(type(row.get("result")) is not bool for row in actual or [])):
        raise ValueError("Boolean parameter class-test behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 1,
            "platform": platform, "profile": "boolean-parameter-class-test",
            "scope": "MarshalByRefObject Boolean class test over first-call null, compatible descendants, incompatible, boxed and array inputs; no formatting effects"}
