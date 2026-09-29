"""Independent observations for padded constructed-generic conditional stores."""

from behavior_oracle import verify_report


COUNTERS = (-(1 << 31), -1, 0, (1 << 31) - 1)
METHODS = ("flag-block", "flag-early", "pair-block", "pair-early")


def observations():
    expected = []
    for owner in ("base", "string", "object"):
        derived = owner != "base"
        expected.append({
            "kind": "constructor", "owner": owner,
            "identityNull": True if derived else None,
            "enabled": False if derived else None,
            "resultFlag": False if derived else None,
            "counter": 0 if derived else None,
            "neighbor": 0 if derived else None,
            "tagNull": True if derived else None,
            "neighborReferenceNull": True if derived else None,
            "paddingZero": True if derived else None,
        })
    for method in METHODS:
        pair = method.startswith("pair-")
        for enabled in (False, True):
            for replacement in (False, True):
                for tag_null in (False, True):
                    for counter in COUNTERS:
                        expected.append({
                            "kind": "call", "method": method,
                            "enabled": enabled, "replacement": replacement,
                            "tagNull": tag_null,
                            "incomingCounter": counter,
                            "failure": "none", "sameReference": True,
                            "returnedDerivedType": True,
                            "enabledAfter": enabled,
                            "resultFlagAfter": replacement if enabled else True,
                            "counterAfter": counter if not pair or enabled else -91,
                            "neighborAfter": 307,
                            "tagAfter": None if tag_null else "tag",
                            "tagSame": True,
                            "identitySame": True,
                            "neighborReferenceSame": True,
                            "paddingIntact": True,
                        })
    for method in METHODS:
        for replacement in (False, True):
            for counter in COUNTERS if method.startswith("pair-") else (0,):
                expected.append({
                    "kind": "null", "method": method,
                    "replacement": replacement,
                    "incomingCounter": counter,
                    "failure": "none", "returnedNull": True,
                })
    for index, counter in enumerate(COUNTERS):
        expected.append({
            "kind": "repeat", "incomingCounter": counter,
            "firstSame": True, "counterAfterDisabled": -91,
            "resultAfterDisabled": True,
            "secondSame": True, "resultAfterFlag": False,
            "thirdSame": True, "counterAfter": counter,
            "resultFlagAfter": index % 2 == 0,
            "enabledAfter": True, "neighborAfter": 307,
            "tagSame": True, "identitySame": True,
            "neighborReferenceSame": True,
            "paddingIntact": True,
        })
    return expected


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "conditional-generic-terminal-store",
        observations(), 6,
        "Six concrete methods; two constructed generic class instances, "
        "four non-generic nullable fluent callers, Boolean gate, ordered byte "
        "and Int32 stores, padded neighboring fields, value-type argument, "
        "reference identity, signed boundaries, and null receiver")
