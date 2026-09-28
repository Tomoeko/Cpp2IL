"""Validate a seeded Unity integer-range call before a reference-array read."""

import json


SEEDS = (0, 1, 12345)
KINDS = ("word-single", "word-many", "word-empty", "word-null",
         "tag-single", "tag-many", "tag-empty", "tag-null")


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("profile") != "range-array-read" or
            report.get("platform") != platform):
        raise ValueError("Range-array read report has wrong target or stage")
    rows = report.get("observations")
    if not isinstance(rows, list) or len(rows) != len(KINDS) * len(SEEDS):
        raise ValueError("Range-array read observation count differs")
    for position, row in enumerate(rows):
        if (not isinstance(row, dict) or
                row.get("seed") != SEEDS[position // len(KINDS)] or
                row.get("kind") != KINDS[position % len(KINDS)]):
            raise ValueError("Range-array read observation ordering differs")
        kind = row["kind"]
        expected_length = 1 if kind.endswith("single") else 3 if kind.endswith("many") else 0 if kind.endswith("empty") else None
        if row.get("length") != expected_length:
            raise ValueError("Range-array read array length differs")
        index = row.get("expectedIndex")
        if expected_length is None:
            expected_exception = "System.NullReferenceException"
            expected_value = None
            if index is not None:
                raise ValueError("A null array consumed a random index")
        elif expected_length == 0:
            expected_exception = "System.IndexOutOfRangeException"
            expected_value = None
            if index != 0:
                raise ValueError("An empty range did not return its equal endpoint")
        else:
            expected_exception = "none"
            if type(index) is not int or not 0 <= index < expected_length:
                raise ValueError("Random.Range returned an out-of-range index")
            labels = (("single",) if kind == "word-single" else
                      ("zero", "one", "two") if kind == "word-many" else
                      ("tag-zero",) if kind == "tag-single" else
                      ("tag-zero", "tag-one", "tag-two"))
            expected_value = labels[index]
        if (row.get("expectedValue") != expected_value or
                row.get("expectedException") != expected_exception or
                row.get("value") != expected_value or
                row.get("exception") != expected_exception):
            raise ValueError("Range-array read value or exception differs")
        if (type(row.get("next")) is not int or
                row.get("next") != row.get("expectedNext") or
                not 0 <= row["next"] < 100000):
            raise ValueError("Range-array read consumed the wrong random state")
    return {"status": "passed", "observations": len(rows), "methods": 4,
            "platform": report["platform"], "profile": "range-array-read",
            "scope": "Seeded range call, reference identity, null and empty arrays, and random state"}
