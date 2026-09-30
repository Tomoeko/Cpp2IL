"""Shared strict JSON checks for independently calculated behavior observations."""

import json


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Behavior report contains duplicate JSON keys")
        result[key] = value
    return result


def _invalid_constant(value):
    raise ValueError("Behavior report contains a nonstandard JSON number: " + value)


def _same_type_and_value(actual, expected):
    if type(actual) is not type(expected):
        return False
    if isinstance(expected, dict):
        return actual.keys() == expected.keys() and all(
            _same_type_and_value(actual[key], value) for key, value in expected.items())
    if isinstance(expected, list):
        return len(actual) == len(expected) and all(
            _same_type_and_value(left, right) for left, right in zip(actual, expected))
    return actual == expected


def read_report(path):
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_unique_object,
                      parse_constant=_invalid_constant)


def verify_report(path, stage, version, profile, expected, methods, scope):
    report = read_report(path)
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (not isinstance(report, dict) or platform is None or report.get("unityVersion") != version or
            report.get("stage") != stage or report.get("platform") != platform or report.get("profile") != profile):
        raise ValueError(profile + " report has the wrong target, stage or profile")
    if not _same_type_and_value(report.get("observations"), expected):
        raise ValueError(profile + " observations differ from the independent typed oracle")
    return {"status": "passed", "observations": len(expected), "methods": methods,
            "platform": platform, "profile": profile, "scope": scope}
