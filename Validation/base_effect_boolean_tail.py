"""Independent observations for ordered declared base calls and guarded tails."""

from behavior_oracle import verify_report


def _call(kind, effect, producer, order, neighbor, witness, *, target="witness",
          alias=None, exception="none", alternate=True, holder_null=False):
    return {
        "kind": kind, "exception": exception, "holderNull": holder_null,
        "effect": effect, "producer": producer, "order": order, "neighbor": neighbor,
        "targetNull": None if holder_null else target == "null",
        "targetSameBefore": None if holder_null else True,
        "targetSameWitness": None if holder_null else target == "witness",
        "targetSameAlternate": None if holder_null else target == "alternate",
        "witnessFlag": witness, "witnessNeighbor": 23,
        "alternateFlag": alternate, "alternateNeighbor": 41,
        "foldedFlag": False, "foldedNeighbor": 47,
        "aliasPresent": alias is not None,
        "aliasSameTarget": None if alias is None or holder_null else alias[4],
        "aliasEffect": None if alias is None else alias[0],
        "aliasProducer": None if alias is None else alias[1],
        "aliasOrder": None if alias is None else alias[2],
        "aliasNeighbor": None if alias is None else alias[3],
    }


def observations():
    null = "System.NullReferenceException"
    return [
        {"kind": "declarations", "methodCount": 13, "baseVirtual": True,
         "producerProtected": True, "enableOverride": True, "disableOverride": True,
         "targetProperty": True, "fieldCounts": True, "targetClass": True,
         "foldedDistinct": True},
        {"kind": "constructors", "plainTargetNull": True, "plainEffect": 0,
         "plainProducer": 0, "plainOrder": 0, "plainNeighbor": 0,
         "enableDefault": True, "disableDefault": True, "targetDefault": True,
         "foldedDefault": True},
        _call("base-only", 1, 0, 1, 0, False, target="null"),
        {"kind": "folded-true", "foldedFlag": True, "foldedNeighbor": 47,
         "witnessFlag": False, "alternateFlag": True},
        {"kind": "folded-false", "foldedFlag": False, "foldedNeighbor": 47,
         "witnessFlag": False, "alternateFlag": True},
        _call("enable", 1, 1, 12, 17, True, alias=(0, 0, 0, 31, True)),
        _call("enable-repeat", 2, 2, 1212, 17, True, alias=(0, 0, 0, 31, True)),
        _call("disable-alias", 1, 1, 12, 31, False, alias=(2, 2, 1212, 17, True)),
        _call("disable-repeat", 2, 2, 1212, 31, False, alias=(2, 2, 1212, 17, True)),
        _call("enable-null-result", 1, 1, 12, 53, False, target="null",
              alias=(2, 2, 1212, 17, False), exception=null),
        _call("enable-null-repeat", 2, 2, 1212, 53, False, target="null",
              alias=(2, 2, 1212, 17, False), exception=null),
        _call("enable-reuse", 3, 3, 121212, 53, True, alias=(2, 2, 1212, 17, True)),
        _call("disable-null-result", 1, 1, 12, 59, True, target="null",
              alias=(2, 2, 1212, 31, False), exception=null),
        _call("disable-reuse", 2, 2, 1212, 59, False, alias=(2, 2, 1212, 31, True)),
        _call("null-holder", None, None, None, None, False, holder_null=True,
              alias=(2, 2, 1212, 17, None), exception=null),
        _call("enable-after-null", 3, 3, 121212, 17, True, alias=(2, 2, 1212, 31, True)),
        _call("enable-new-target", 4, 4, 12121212, 17, True, target="alternate",
              alias=(2, 2, 1212, 31, False)),
        _call("disable-new-alias", 3, 3, 121212, 31, True, target="alternate",
              alias=(4, 4, 12121212, 17, True), alternate=False),
    ]


def verify(path, stage, version):
    return verify_report(path, stage, version, "base-effect-boolean-tail",
                         observations(), 13,
                         "declared base dispatch, ordered effectful producer, Boolean tails, aliases, null failures and reuse")
