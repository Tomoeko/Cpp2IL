"""Independent effects for complete field-backed Boolean array fill loops."""

from behavior_oracle import verify_report


def _seed(length):
    return [index % 3 != 1 for index in range(length)]


def _call(kind, before, witness_before, literal, value, neighbor, flag,
          alias=None, holder_null=False):
    succeeds = not holder_null and before is not None
    after = [False if literal else value] * len(before) if succeeds else before
    witness_after = after if succeeds else witness_before
    return {
        "kind": kind, "operation": "literal-false" if literal else "parameter",
        "value": None if literal else value,
        "exception": "none" if succeeds else "System.NullReferenceException",
        "before": before, "after": None if holder_null else after,
        "witnessBefore": witness_before, "witnessAfter": witness_after,
        "holderIsNull": holder_null,
        "valuesSameBefore": None if holder_null else True,
        "valuesSameWitness": None if holder_null else before is not None,
        "neighborAfter": None if holder_null else neighbor,
        "flagAfter": None if holder_null else flag,
        "aliasSameWitness": None if alias is None else True,
        "aliasAfter": None if alias is None else witness_after,
        "aliasNeighborAfter": None if alias is None else alias[0],
        "aliasFlagAfter": None if alias is None else alias[1],
    }


def observations():
    result = [
        {"kind": "declarations", "sealedClass": True, "threeFields": True,
         "twoMethods": True, "oneConstructor": True, "valuesField": True,
         "neighborField": True, "flagField": True, "literalSignature": True,
         "parameterSignature": True},
        {"kind": "constructor", "created": True, "valuesIsNull": True,
         "neighbor": 0, "flag": False},
    ]
    for length in (0, 1, 2, 3, 31, 32, 33, 127, 128, 129):
        before = _seed(length)
        for suffix, literal, value in (("true", False, True), ("false", False, False),
                                       ("true-again", False, True), ("literal", True, False),
                                       ("literal-repeat", True, False)):
            result.append(_call("length-" + str(length) + "-" + suffix, before,
                                before, literal, value, 17, True, (23, False)))
            before = [False if literal else value] * length
    result.append(_call("shared-true", _seed(7), _seed(7), False, True, 31, False, (37, True)))
    result.append(_call("shared-literal", [True] * 7, [True] * 7, True, False, 37, True, (31, False)))
    result.append(_call("null-array-parameter", None, _seed(5), False, True, 41, True))
    result.append(_call("null-array-literal", None, _seed(5), True, False, 41, True))
    result.append(_call("reused-array-true", _seed(5), _seed(5), False, True, 41, True))
    result.append(_call("reused-array-false", [True] * 5, [True] * 5, False, False, 41, True))
    result.append(_call("reused-array-literal", [False] * 5, [False] * 5, True, False, 41, True))
    result.append(_call("null-holder-parameter", None, [False] * 5, False, True, None, None, holder_null=True))
    result.append(_call("null-holder-literal", None, [False] * 5, True, False, None, None, holder_null=True))
    return result


def verify(path, stage, version):
    return verify_report(path, stage, version, "boolean-array-fill-loop", observations(), 3,
                         "field-reloading Boolean fills, empty and boundary lengths, aliases, null failures and reuse")
