"""Independent observations for nullable Boolean-gated fluent field stores."""

from behavior_oracle import verify_report


VALUES = (0, 1, 127, 128, 255)
COUNTERS = (-(1 << 31), -1, 0, (1 << 31) - 1)


def observations():
    expected = [{"kind": "constructor", "enabled": False, "value": 0,
                 "counter": 0, "resultFlag": False, "neighbor": 0,
                 "referenceNull": True}]
    for enabled in (False, True):
        for value in VALUES:
            for counter in COUNTERS:
                expected.append({
                    "kind": "byte", "enabled": enabled, "incoming": value,
                    "counterBefore": counter, "failure": "none",
                    "sameReference": True, "enabledAfter": enabled,
                    "valueAfter": value if enabled else 17,
                    "counterAfter": counter, "resultFlagAfter": True,
                    "neighborAfter": 307, "referenceSame": True,
                })
    for enabled in (False, True):
        for counter in COUNTERS:
            for replacement in (False, True):
                expected.append({
                    "kind": "pair", "enabled": enabled, "incoming": counter,
                    "replacement": replacement, "failure": "none",
                    "sameReference": True, "enabledAfter": enabled,
                    "valueAfter": 17, "counterAfter": counter if enabled else -91,
                    "resultFlagAfter": replacement if enabled else True,
                    "neighborAfter": 307, "referenceSame": True,
                })
    for value in VALUES:
        expected.append({"kind": "null-byte", "incoming": value,
                         "failure": "none", "returnedNull": True})
    for counter in COUNTERS:
        for replacement in (False, True):
            expected.append({"kind": "null-pair", "incoming": counter,
                             "replacement": replacement, "failure": "none",
                             "returnedNull": True})
    for index, value in enumerate(VALUES):
        expected.append({
            "kind": "repeat", "incoming": value,
            "firstSame": True, "valueAfterDisabled": 17,
            "secondSame": True, "valueAfterEnabled": 255 - value,
            "thirdSame": True, "counterAfter": COUNTERS[index % len(COUNTERS)],
            "resultFlagAfter": index % 2 == 0, "enabledAfter": True,
            "neighborAfter": 307, "referenceSame": True,
        })
    return expected


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "conditional-boolean-store", observations(), 3,
        "Three concrete methods; null and false-branch returns, conditional byte and "
        "Int32/Boolean stores, signed boundaries, alias identity, repeated calls, "
        "and unchanged neighboring fields")
