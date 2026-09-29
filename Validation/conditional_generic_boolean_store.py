"""Independent observations for nullable constructed-generic Boolean-gated stores."""

from behavior_oracle import verify_report


COUNTERS = (-(1 << 31), -1, 0, (1 << 31) - 1)


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
        })
    for enabled in (False, True):
        for replacement in (False, True):
            for tag_null in (False, True):
                for counter in COUNTERS:
                    expected.append({
                        "kind": "flag", "enabled": enabled,
                        "replacement": replacement, "tagNull": tag_null,
                        "incomingCounter": counter, "failure": "none",
                        "sameReference": True, "returnedDerivedType": True,
                        "enabledAfter": enabled,
                        "resultFlagAfter": replacement if enabled else True,
                        "counterAfter": counter, "neighborAfter": 307,
                        "tagAfter": None if tag_null else "tag", "tagSame": True,
                        "identitySame": True, "neighborReferenceSame": True,
                    })
    for enabled in (False, True):
        for counter in COUNTERS:
            for replacement in (False, True):
                for tag_null in (False, True):
                    expected.append({
                        "kind": "pair", "enabled": enabled,
                        "replacement": replacement, "tagNull": tag_null,
                        "incomingCounter": counter, "failure": "none",
                        "sameReference": True, "returnedDerivedType": True,
                        "enabledAfter": enabled,
                        "resultFlagAfter": replacement if enabled else True,
                        "counterAfter": counter if enabled else -91,
                        "neighborAfter": 307,
                        "tagAfter": None if tag_null else "tag", "tagSame": True,
                        "identitySame": True, "neighborReferenceSame": True,
                        "argumentAfter": counter,
                    })
    for replacement in (False, True):
        expected.append({"kind": "null-flag", "replacement": replacement,
                         "failure": "none", "returnedNull": True})
    for counter in COUNTERS:
        for replacement in (False, True):
            expected.append({"kind": "null-pair", "incomingCounter": counter,
                             "replacement": replacement, "failure": "none",
                             "returnedNull": True})
    for index, counter in enumerate(COUNTERS):
        expected.append({
            "kind": "repeat", "incomingCounter": counter,
            "firstSame": True, "counterAfterDisabled": -91,
            "resultAfterDisabled": True, "secondSame": True,
            "resultAfterFlag": False, "thirdSame": True,
            "counterAfter": counter, "resultFlagAfter": index % 2 == 0,
            "enabledAfter": True, "neighborAfter": 307,
            "tagSame": True, "identitySame": True,
            "neighborReferenceSame": True,
        })
    return expected


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "conditional-generic-boolean-store", observations(), 4,
        "Four concrete methods; constructed generic receiver and base-class return, "
        "nullable Boolean gate, byte and Int32 field writes, value-type argument, "
        "signed boundaries, same-reference return, tag/base/reference identity, "
        "and unchanged neighboring fields")
