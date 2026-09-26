"""Independent argument-slot and shared-call oracle for the exact target."""

import json


def observations():
    rows = []
    for value in (-(2**31), -17, 0, 19, 2**31 - 1):
        bits = -(2**31) if value == 0 else value
        rows.extend((
            {"kind": "call", "value": value, "returned": 43,
             "fields": [value, -31, 37, -41, 43]},
            {"kind": "tail", "value": value, "fields": [47, -53, 59, -61, value]},
            {"kind": "sixth", "value": value, "returned": value},
            {"kind": "shared", "value": value, "exception": "none",
             "integer": [value] * 3, "floating": [bits] * 3, "twin": [bits] * 3, "neighbors": [0] * 4},
        ))
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "shared-abi" or report.get("platform") != platform):
        raise ValueError("Shared ABI report has wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Shared ABI observations differ from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 15,
            "platform": platform, "profile": "shared-abi",
            "scope": "shared float stores and integer call controls, incoming/outgoing stack arguments and tail calls"}
