"""Independent typed oracle for a final class override and a shadow getter."""

from behavior_oracle import verify_report


def observations():
    rows = [
        {"kind": "default-state", "flag": False, "baseValue": False,
         "concreteValue": False, "baseReferenceNull": True,
         "baseNeighbor": 0, "referenceNull": True, "neighbor": 0},
        {"kind": "default-shadow", "flag": False, "shadowFlag": False,
         "baseValue": False, "middleValue": False, "shadowValue": False,
         "baseReferenceNull": True, "baseNeighbor": 0,
         "referenceNull": True, "neighbor": 0, "shadowNeighbor": 0},
        {"kind": "null-base", "failure": "System.NullReferenceException"},
        {"kind": "null-shadow", "failure": "System.NullReferenceException"},
    ]
    for flag in (False, True):
        rows.append({
            "kind": "state", "inputFlag": flag,
            "baseValue": flag, "concreteValue": flag, "repeatValue": flag,
            "aliasSame": True, "flagAfter": flag,
            "baseReferenceSame": True, "baseNeighbor": -31,
            "referenceSame": True, "neighbor": 47,
        })
    for flag in (False, True):
        for shadow_flag in (False, True):
            rows.append({
                "kind": "shadow", "inputFlag": flag,
                "inputShadowFlag": shadow_flag,
                "baseValue": flag, "middleValue": flag,
                "shadowValue": shadow_flag,
                "repeatBaseValue": flag, "repeatShadowValue": shadow_flag,
                "baseAliasSame": True, "middleAliasSame": True,
                "flagAfter": flag, "shadowFlagAfter": shadow_flag,
                "baseReferenceSame": True, "baseNeighbor": -31,
                "referenceSame": True, "neighbor": 47,
                "shadowNeighbor": -59,
            })
    return rows


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "final-override-boolean-getter", observations(), 6,
        "Final virtual Boolean override dispatch versus a new shadow getter; "
        "default fields, null receivers, repeated reads and unchanged references")
