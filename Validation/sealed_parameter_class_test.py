"""Independent finite oracle for sealed parameter class Boolean/reference tests."""

import json


def observations():
    compatible = {"builder", "builder-repeat", "second-builder"}
    return [
        {"kind": kind, "testResult": kind in compatible,
         "sameReference": kind in compatible, "nullResult": kind not in compatible,
         "builderText": "seed", "failure": "none", "hresult": 0, "formatCalls": 0}
        for kind in ("null-first", "object", "formatting-trap", "string", "boxed-int",
                     "object-vector", "multidimensional", "builder-vector", "builder",
                     "builder-repeat", "second-builder", "null-repeat")
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    actual = report.get("observations")
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("platform") != platform or
            report.get("profile") != "sealed-parameter-class-test" or
            actual != observations() or
            any(type(row.get(key)) is not bool for row in actual or []
                for key in ("testResult", "sameReference", "nullResult"))):
        raise ValueError("Sealed parameter class-test behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(actual), "methods": 2,
            "platform": platform, "profile": "sealed-parameter-class-test",
            "scope": "StringBuilder Boolean/reference class tests over first-call and repeated null, exact instances, incompatible, boxed and array inputs; reference identity and no formatting or builder mutation"}
