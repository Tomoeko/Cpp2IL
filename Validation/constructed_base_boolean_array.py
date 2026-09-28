"""Independent behavior oracle for a Boolean array owned by a derived class."""

import json

from field_boolean_array import observations as store_observations


def observations():
    stores = store_observations()
    expected = []
    for offset in range(0, len(stores), 2):
        true_store, false_store = stores[offset:offset + 2]
        expected.extend((true_store, false_store))
        before = true_store["before"]
        index = true_store["index"]
        exception = true_store["exception"]
        result = before[index] if exception == "none" else None
        read = dict(true_store)
        read.update({
            "kind": "read", "result": result,
            "after": before,
            "aliasAfter": before if true_store["aliasSharesArray"] else None,
        })
        expected.append(read)
    return expected


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or
            report.get("profile") != "constructed-base-boolean-array" or
            report.get("platform") != platform):
        raise ValueError("Constructed-base Boolean-array report has the wrong target or stage")
    expected = observations()
    if report.get("observations") != expected:
        raise ValueError("Constructed-base Boolean-array behavior differs from the oracle")
    return {"status": "passed", "observations": len(expected), "methods": 5,
            "platform": report["platform"], "profile": "constructed-base-boolean-array",
            "scope": "fieldless constructed base, Boolean reads and stores, null and bounds failures, aliasing"}
