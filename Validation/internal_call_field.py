"""Independent observations for metadata-bound engine field calls."""

from behavior_oracle import verify_report


def _call(kind, operation, active, name_before, name_after, label, neighbor,
          result=None, alias=None, target_null=False, holder_null=False,
          exception="none"):
    return {
        "kind": kind, "operation": operation, "exception": exception,
        "result": result, "activeBefore": active, "activeAfter": active,
        "nameBefore": name_before, "nameAfter": name_after,
        "holderIsNull": holder_null,
        "targetIsNull": None if holder_null else target_null,
        "targetSameBefore": None if holder_null else True,
        "targetSameWitness": None if holder_null else not target_null,
        "labelAfter": None if holder_null else label,
        "neighborAfter": None if holder_null else neighbor,
        "aliasSameWitness": None if alias is None else True,
        "aliasLabelAfter": None if alias is None else alias[0],
        "aliasNeighborAfter": None if alias is None else alias[1],
    }


def observations():
    primary = ("neutral-renamed", 17)
    shared = ("neutral-alias", 23)
    null_failure = "System.NullReferenceException"
    return [
        {"kind": "declarations", "sealedClass": True, "threeFields": True,
         "twoMethods": True, "oneConstructor": True, "targetField": True,
         "labelField": True, "neighborField": True, "readSignature": True,
         "renameSignature": True},
        {"kind": "constructor", "created": True, "targetIsNull": True,
         "labelIsNull": True, "neighbor": 0},
        _call("read-active", "read", True, "neutral-start", "neutral-start",
              "neutral-label", 17, result=True),
        _call("read-inactive", "read", False, "neutral-start", "neutral-start",
              "neutral-label", 17, result=False),
        _call("rename-empty", "rename", False, "neutral-start", "", "", 17),
        _call("rename-empty-repeat", "rename", False, "", "", "", 17),
        _call("rename-nonempty", "rename", False, "", "neutral-renamed",
              "neutral-renamed", 17),
        _call("rename-nonempty-repeat", "rename", False, "neutral-renamed",
              "neutral-renamed", "neutral-renamed", 17),
        _call("rename-shared-target", "rename", False, "neutral-renamed",
              "neutral-alias", "neutral-alias", 23, alias=primary),
        _call("read-shared-target", "read", False, "neutral-alias",
              "neutral-alias", "neutral-renamed", 17, result=False, alias=shared),
        _call("read-null-target", "read", False, "neutral-alias", "neutral-alias",
              "neutral-missing", 31, alias=primary, target_null=True,
              exception=null_failure),
        _call("rename-null-target", "rename", False, "neutral-alias", "neutral-alias",
              "neutral-missing", 31, alias=primary, target_null=True,
              exception=null_failure),
        _call("read-reused-target", "read", False, "neutral-alias", "neutral-alias",
              "neutral-missing", 31, result=False, alias=primary),
        _call("rename-reused-target", "rename", False, "neutral-alias",
              "neutral-missing", "neutral-missing", 31, alias=primary),
        _call("read-null-holder", "read", False, "neutral-missing", "neutral-missing",
              None, None, alias=primary, holder_null=True, exception=null_failure),
        _call("rename-null-holder", "rename", False, "neutral-missing", "neutral-missing",
              None, None, alias=primary, holder_null=True, exception=null_failure),
        _call("read-reused-holder", "read", True, "neutral-missing", "neutral-missing",
              "neutral-renamed", 17, result=True, alias=shared),
        _call("rename-reused-holder", "rename", True, "neutral-missing",
              "neutral-renamed", "neutral-renamed", 17, alias=shared),
    ]


def verify(path, stage, version):
    return verify_report(path, stage, version, "internal-call-field",
                         observations(), 3,
                         "engine Boolean getter and string setter through fields, aliases, null failures and reuse")
