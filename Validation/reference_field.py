"""Independent oracle for bounded reference-field reads."""

import json


def observations():
    expected = [{"kind": "constructors", "boxCreated": True, "outerCreated": True,
                 "derivedBoxCreated": True, "derivedOuterCreated": True}]
    for label, value in (("value", "qqq"), ("empty", ""), ("null-value", None)):
        for prefix in ("direct", "nested"):
            expected.append({"kind": prefix + "-" + label, "result": value,
                             "sameReference": True, "exception": "none"})
    for kind in ("direct-null-owner", "nested-null-inner", "nested-null-outer"):
        expected.append({"kind": kind, "result": None, "sameReference": False,
                         "exception": "System.NullReferenceException"})
    for kind in ("direct-derived-value", "nested-derived-value"):
        expected.append({"kind": kind, "result": "dddd", "sameReference": True,
                         "exception": "none"})
    for kind in ("direct-derived-null-value", "nested-derived-null-value"):
        expected.append({"kind": kind, "result": None, "sameReference": True,
                         "exception": "none"})
    expected.append({"kind": "nested-derived-null-inner", "result": None,
                     "sameReference": False, "exception": "System.NullReferenceException"})
    expected.append({"kind": "derived-layout", "boxMarker": -(1 << 31),
                     "outerMarker": (1 << 63) - 1})
    expected.append({"kind": "neighbor-arrays", "boxPrefix": [-(1 << 31), 0],
                     "boxSuffix": [(1 << 31) - 1, -17], "outerPrefix": [11, 13],
                     "outerSuffix": [-19, -23], "sameBoxPrefix": True,
                     "sameBoxSuffix": True, "sameOuterPrefix": True,
                     "sameOuterSuffix": True})
    for label, value in (("boxed", -(1 << 31)), ("string", "ooo"),
                         ("null-value", None)):
        for prefix in ("object-direct", "object-nested"):
            expected.append({"kind": prefix + "-" + label, "result": value,
                             "sameReference": True, "exception": "none"})
    for kind in ("object-direct-null-owner", "object-nested-null-inner",
                 "object-nested-null-outer"):
        expected.append({"kind": kind, "result": None, "sameReference": False,
                         "exception": "System.NullReferenceException"})
    for label, value in (("values", [-(1 << 31), 0, (1 << 31) - 1]),
                         ("empty", []), ("null-value", None)):
        for prefix in ("array-direct", "array-nested"):
            expected.append({"kind": prefix + "-" + label, "result": value,
                             "sameReference": True, "exception": "none"})
    for kind in ("array-direct-null-owner", "array-nested-null-inner",
                 "array-nested-null-outer"):
        expected.append({"kind": kind, "result": None, "sameReference": False,
                         "exception": "System.NullReferenceException"})
    for kind, value in (("self-text-value", "tttt"),
                        ("self-object-boxed", (1 << 31) - 1),
                        ("self-array-values", [-(1 << 31), (1 << 31) - 1])):
        expected.append({"kind": kind, "result": value, "sameReference": True,
                         "exception": "none"})
    for kind in ("self-text-null-value", "self-object-null-value", "self-array-null-value"):
        expected.append({"kind": kind, "result": None, "sameReference": True,
                         "exception": "none"})
    for kind in ("self-text-null-owner", "self-object-null-owner", "self-array-null-owner"):
        expected.append({"kind": kind, "result": None, "sameReference": False,
                         "exception": "System.NullReferenceException"})
    expected.append({"kind": "shared-constructors", "boxCreated": True,
                     "outerCreated": True})
    for kind in ("shared-direct-value", "shared-nested-value"):
        expected.append({"kind": kind, "result": "ssss", "sameReference": True,
                         "exception": "none"})
    for kind in ("shared-direct-null-value", "shared-nested-null-value"):
        expected.append({"kind": kind, "result": None, "sameReference": True,
                         "exception": "none"})
    for kind in ("shared-direct-null-owner", "shared-nested-null-inner",
                 "shared-nested-null-outer"):
        expected.append({"kind": kind, "result": None, "sameReference": False,
                         "exception": "System.NullReferenceException"})
    expected.append({"kind": "shared-neighbors", "boxPrefix": [-31],
                     "boxSuffix": [37], "outerPrefix": [-41], "outerSuffix": [43],
                     "sameBoxPrefix": True, "sameBoxSuffix": True,
                     "sameOuterPrefix": True, "sameOuterSuffix": True})
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != version or report.get("stage") != stage or report.get("profile") != "reference-field":
        raise ValueError("Reference-field report has the wrong version, stage or profile")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("Reference-field native observations require a Windows player")
    expected = observations()
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Reference-field behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 17,
            "platform": report["platform"], "profile": "reference-field",
            "scope": "direct, self and one-level nested string/object/int-array field reads with null owners; not whole-program equivalence"}
