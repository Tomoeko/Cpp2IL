"""Independent bit-preserving oracle for four guarded scalar parameter stores."""

from copy import deepcopy

from behavior_oracle import verify_report
from scalar_float_selection import _argument_bits


PROFILE = "native-nested-scalar-parameter-store"
ASSEMBLY = "NativeNestedScalarParameterStoreFixture"
VALUES = {
    "boolean": (False, True),
    "int32": (-(1 << 31), -(1 << 31) + 1, -1, 0, 1, (1 << 31) - 2, (1 << 31) - 1),
    "uint32": (0, 1, 255, 65535, (1 << 31) - 1, 1 << 31, (1 << 32) - 1),
    "single": (0, 0x80000000, 1, 0x80000001, 0x007fffff, 0x00800000,
               0x7f7fffff, 0x7f800000, 0xff800000, 0x7fc00001, 0xffc00123,
               0x7f800002, 0x3fc00000),
}
FIELDS = {"boolean": "enabled", "int32": "signed", "uint32": "unsigned", "single": "amountBits"}
NULL_FAILURE = "System.NullReferenceException"


def _fresh():
    return {"firstTarget": "first", "secondTarget": "second",
            "first": {"before": 11, "enabled": True, "signed": -13, "unsigned": 17,
                      "amountBits": 0x40400000, "after": 29},
            "second": {"before": 37, "enabled": False, "signed": 41, "unsigned": 43,
                       "amountBits": 0x40800000, "after": 47}}


def _record(kind, operation, receiver, incoming, state, stage):
    before = deepcopy(state)
    target = "null" if receiver == "null" else state[receiver + "Target"]
    exception = NULL_FAILURE if target == "null" else "none"
    transported = _argument_bits(incoming, 32, stage) if operation == "single" else "not-used"
    if exception == "none":
        state[target][FIELDS[operation]] = transported if operation == "single" else incoming
    return {"kind": kind, "operation": operation, "receiver": receiver, "incoming": incoming,
            "transportedIncomingBits": transported, "exception": exception, "before": before, "after": deepcopy(state)}


def observations(stage="player"):
    prefix = ASSEMBLY + "."
    field_types = {"Cell.Before": "System.Byte", "Cell.Enabled": "System.Boolean",
                   "Cell.Signed": "System.Int32", "Cell.Unsigned": "System.UInt32",
                   "Cell.Amount": "System.Single", "Cell.After": "System.Byte", "Holder.Target": prefix + "Cell"}
    rows = [{"kind": "declarations", "methods": 6, "fields": 7, "properties": 0, "types": 2,
             "signatures": {"Holder.StoreBoolean": ["System.Void", "System.Boolean"],
                            "Holder.StoreInt32": ["System.Void", "System.Int32"],
                            "Holder.StoreUInt32": ["System.Void", "System.UInt32"],
                            "Holder.StoreSingle": ["System.Void", "System.Single"]},
             "fieldTypes": field_types, "fieldAccess": {name: "Public" for name in field_types}},
            {"kind": "defaults", "targetNull": True,
             "cell": {"before": 0, "enabled": False, "signed": 0, "unsigned": 0, "amountBits": 0, "after": 0}}]
    for operation, values in VALUES.items():
        for index, incoming in enumerate(values):
            for mode in ("value", "null-target", "null-owner"):
                state = _fresh()
                if mode == "null-target": state["firstTarget"] = "null"
                receiver = "null" if mode == "null-owner" else "first"
                rows.append(_record(operation + "-" + mode + "-" + str(index), operation, receiver, incoming, state, stage))
    state = _fresh()
    rows.append(_record("reuse-signed-first", "int32", "first", -7, state, stage))
    rows.append(_record("reuse-unsigned-second", "uint32", "second", 0xffffffff, state, stage))
    rows.append(_record("reuse-negative-zero", "single", "first", 0x80000000, state, stage))
    rows.append(_record("reuse-boolean-second", "boolean", "second", False, state, stage))
    state["firstTarget"] = "second"
    rows.append(_record("retarget-first-to-second", "int32", "first", (1 << 31) - 1, state, stage))
    rows.append(_record("shared-target-single", "single", "second", 0x7fc00001, state, stage))
    state["firstTarget"] = "null"
    rows.append(_record("reuse-null-target", "boolean", "first", True, state, stage))
    state["firstTarget"] = "first"
    rows.append(_record("retarget-first-back", "uint32", "first", 0, state, stage))
    rows.append(_record("reuse-null-owner", "single", "null", 0xff800000, state, stage))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, PROFILE, observations(stage), 6,
                         "All six methods; four bit-preserving scalar stores, null failures before effects, "
                         "signed/unsigned boundaries, IEEE Single payloads, independently observed editor binary32 "
                         "argument quieting and native preservation, aliases, fresh captures and unchanged neighbors; "
                         "no altered floating-control/status claim")
