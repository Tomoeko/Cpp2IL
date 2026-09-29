"""Independent oracle for signed and unsigned enum array reads through a field."""

import json


SIGNED = (-7, 0, -(1 << 31) + 17, 42, 253)
UNSIGNED = ((1 << 32) - 1, 0, (1 << 31) + 17, 42, 253)


def observations(stage="player"):
    rows = []
    for kind in ("null-owner", "null-array", "empty", "single", "double", "mixed"):
        length = {"single": 1, "double": 2, "mixed": 5}.get(kind, 0)
        operations = [("read-first", None, 0), ("read-fixed", None, 2)]
        operations.extend(("read-at", index, index)
                          for index in (-(1 << 31), -1, 0, 1, length - 1, length, (1 << 31) - 1))
        for operation, index, access_index in operations:
            for element, values in (("signed", SIGNED), ("unsigned", UNSIGNED)):
                has_owner = kind != "null-owner"
                has_array = kind not in ("null-owner", "null-array")
                valid = has_array and 0 <= access_index < length
                if not has_owner or not has_array:
                    exception = "System.NullReferenceException"
                    message = "Object reference not set to an instance of an object"
                    if stage == "player":
                        message += "."
                    hresult = -2147467261
                elif not valid:
                    exception = "System.IndexOutOfRangeException"
                    message = "Index was outside the bounds of the array."
                    hresult = -2146233080
                else:
                    exception, message, hresult = "none", None, None
                initial = list(values[:length]) if has_array else None
                rows.append({
                    "kind": kind, "element": element, "operation": operation, "index": index,
                    "result": values[access_index] if valid else None,
                    "exception": exception, "message": message, "hresult": hresult,
                    "innerException": False, "before": initial, "after": initial,
                    "aliasAfter": initial, "sameArray": has_owner,
                    "aliasSharesArray": has_array,
                    "ownerBefore": -53 if has_owner else None,
                    "spacer": -71 if has_owner and element == "unsigned" else None,
                    "ownerAfter": 59 if has_owner else None,
                    "aliasBefore": 61 if has_owner else None,
                    "aliasAfterMarker": -67 if has_owner else None,
                })
    return rows


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("profile") != "enum-field-array" or
            report.get("platform") != platform):
        raise ValueError("Enum-field-array report has the wrong version, stage, profile or platform")
    expected = observations(stage)
    if report.get("observations") != expected:
        raise ValueError("Enum-field-array behavior differs from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 8,
            "platform": report["platform"], "profile": "enum-field-array",
            "scope": "signed/unsigned enum field reads, null and bounds exceptions, identity and unchanged state"}
