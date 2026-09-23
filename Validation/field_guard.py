"""Independent oracle for signed field access, Boolean reads/clears and null receivers."""

import json


def observations():
    expected = [{"kind": str(value), "result": value, "exception": "none"}
                for value in (-(1 << 31), -17, 0, 17, (1 << 31) - 1)]
    expected.append({"kind": "null", "result": None, "exception": "System.NullReferenceException"})
    expected.extend({"kind": "write:" + str(value), "result": value, "exception": "none"}
                    for value in (-(1 << 31), -17, 0, 17, (1 << 31) - 1))
    expected.append({"kind": "write:null", "result": None, "exception": "System.NullReferenceException"})
    for initial in (-(1 << 31), 0, (1 << 31) - 1):
        expected.append({"kind": "clear:" + str(initial), "result": 0, "exception": "none"})
    expected.append({"kind": "clear:null", "result": None,
                     "exception": "System.NullReferenceException"})
    for value in (-(1 << 63), -17, 0, 17, (1 << 63) - 1):
        expected.append({"kind": "long:" + str(value), "result": value, "exception": "none"})
    expected.append({"kind": "long:null", "result": None, "exception": "System.NullReferenceException"})
    for value in (-(1 << 63), -17, 0, 17, (1 << 63) - 1):
        expected.append({"kind": "long-write:" + str(value), "result": value, "exception": "none"})
    expected.append({"kind": "long-write:null", "result": None,
                     "exception": "System.NullReferenceException"})
    for initial in (False, True):
        expected.append({"kind": "bool-read:" + str(initial), "result": initial, "exception": "none"})
        expected.append({"kind": "bool-instance-read:" + str(initial), "result": initial,
                         "exception": "none"})
        expected.append({"kind": "bool-clear:" + str(initial), "result": False, "exception": "none"})
    expected.append({"kind": "bool-read:null", "result": None,
                     "exception": "System.NullReferenceException"})
    for kind in ("box-null", "reader-null"):
        expected.append({"kind": "bool-instance-read:" + kind, "result": None,
                         "exception": "System.NullReferenceException"})
    expected.append({"kind": "bool-clear:null", "result": None,
                     "exception": "System.NullReferenceException"})
    for initial in (False, True):
        expected.append({"kind": "bool-call-branch:" + str(initial).lower(),
                         "result": 17 if initial else -17,
                         "valueAfter": initial, "neighborAfter": 23, "exception": "none"})
    expected.append({"kind": "bool-call-branch:null", "result": None,
                     "valueAfter": None, "neighborAfter": None,
                     "exception": "System.NullReferenceException"})
    for initial in (-(1 << 31), 0, (1 << 31) - 1):
        expected.append({"kind": "nested-clear:" + str(initial), "result": 0,
                         "exception": "none"})
    for kind in ("inner-null", "outer-null"):
        expected.append({"kind": "nested-clear:" + kind, "result": None,
                         "exception": "System.NullReferenceException"})
    for initial in (False, True):
        for set_value in (True, False):
            expected.append({"kind": "nested-bool:" + str(set_value).lower() + ":" + str(initial),
                             "result": set_value, "ownerNeighbor": 81, "innerNeighbor": -82,
                             "exception": "none"})
    for set_value in (True, False):
        expected.append({"kind": "nested-bool:" + str(set_value).lower() + ":inner-null",
                         "result": None, "ownerNeighbor": 81, "innerNeighbor": None,
                         "exception": "System.NullReferenceException"})
        expected.append({"kind": "nested-bool:" + str(set_value).lower() + ":outer-null",
                         "result": None, "ownerNeighbor": None, "innerNeighbor": None,
                         "exception": "System.NullReferenceException"})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "field-guard":
        raise ValueError("Field-guard report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Field-guard native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Field-guard behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 12,
            "platform": report["platform"], "profile": "field-guard",
            "scope": "signed 32/64-bit access, direct and nested zero stores, Boolean reads and guarded call branches, and nested Boolean literal stores with null receivers; not whole-program equivalence"}
