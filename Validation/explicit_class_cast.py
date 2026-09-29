"""Finite independent behavior oracle for an explicit reference cast."""

import json


def observations(stage="player"):
    rows = [
        ("null", "null", None),
        ("exact", "System.Exception", None),
        ("subtype", "System.InvalidOperationException", None),
        ("exact-repeat", "System.Exception", None),
        ("string", "null", "String"),
        ("boxed-int", "null", "Int32"),
        ("string-repeat", "null", "String"),
        ("string-array", "null", "String"),
        ("rectangular-array", "null", "Double"),
        ("generic", "null", "GenericSource`1"),
        ("nested", "null", "NestedSource"),
        ("boxed-value", "null", "NumericSource"),
        ("long-name", "null", "IncompatibleClassWithALongNameToExerciseMoreThanOneNativeStringBufferGrowthDuringCastFailure"),
        ("long-name-repeat", "null", "IncompatibleClassWithALongNameToExerciseMoreThanOneNativeStringBufferGrowthDuringCastFailure"),
    ]
    return [
        {"kind": kind, "sameReference": True, "resultType": result,
         "failure": "System.InvalidCastException" if source else "none",
         "message": ("Specified cast is not valid." if stage == "editor" else
                     f"Unable to cast object of type '{source}' to type 'Exception'.") if source else "none",
         "hresult": -2147467262 if source else 0, "innerException": "none",
         "freshFailure": True, "userFormatCalls": 0}
        for kind, result, source in rows
    ]


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "explicit-class-cast" or
            report.get("platform") != platform or
            report.get("observations") != observations(stage)):
        raise ValueError("Explicit reference cast behavior differs from the independent oracle")
    return {"status": "passed", "observations": 14, "methods": 1,
            "platform": platform, "profile": "explicit-class-cast",
            "scope": "Exception reference cast identity and failure fields; arrays, generics, nesting, boxed values, repeated and long names"}
