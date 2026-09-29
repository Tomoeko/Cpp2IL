"""Independent expectations for guarded Boolean and Int32 enum accessor reads."""

from behavior_oracle import verify_report


CODES = (-(1 << 31), -(1 << 31) + 1, -65536, -32769, -32768, -129, -128, -1,
         0, 1, 2, 127, 128, 255, 256, 32767, 32768, 65535, 65536,
         (1 << 31) - 2, (1 << 31) - 1)


def observations():
    expected = [
        {"kind": "constructor", "owner": "state", "flag": False, "code": 0,
         "neighbor": 0, "referenceNull": True, "arrayNull": True},
        {"kind": "constructor", "owner": "holder", "stateNull": True},
        {"kind": "constructor", "owner": "reader", "stateNull": True},
    ]
    for flag in (False, True):
        for code in CODES:
            for neighbor in (-137, 0, 911):
                for reference_null in (False, True):
                    array = [neighbor, code, (1 << 31) - 1 if flag else -(1 << 31), ~code]
                    expected.append({
                        "kind": "read", "flag": flag, "code": code, "neighbor": neighbor,
                        "referenceNull": reference_null, "arrayBefore": list(array),
                        "flagFirst": flag, "flagFirstFailure": "none",
                        "codeFirst": code, "codeFirstFailure": "none",
                        "flagSecond": flag, "flagSecondFailure": "none",
                        "codeSecond": code, "codeSecondFailure": "none",
                        "flagAfter": flag, "codeAfter": code, "neighborAfter": neighbor,
                        "referenceSame": True, "stateSame": True, "aliasStateSame": True,
                        "arraySame": True, "arrayAfter": list(array),
                    })
    for route in ("state-flag", "state-code", "receiver-flag", "receiver-code"):
        expected.append({"kind": "null", "route": route, "value": None,
                         "valueFailure": "System.NullReferenceException"})
    return expected


def verify(path, stage, version):
    return verify_report(path, stage, version, "guarded-scalar-accessor", observations(), 7,
                         "Seven constructor/accessor/reader bodies; both flags, 21 Int32 enum boundaries, "
                         "two readers aliasing one state, unchanged private fields/neighbors/reference/array identity and elements, "
                         "and null failures; "
                         "reflection is validation-only setup and observation")
