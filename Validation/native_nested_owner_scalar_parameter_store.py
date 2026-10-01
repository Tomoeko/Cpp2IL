"""Independent oracle for a scalar store owned by an original nested class."""

from copy import deepcopy

from behavior_oracle import verify_report
from native_nested_scalar_parameter_store import VALUES
from scalar_float_selection import _argument_bits


PROFILE = "native-nested-owner-scalar-parameter-store"
ASSEMBLY = "NativeNestedOwnerScalarParameterStoreFixture"


def _fresh():
    return {"firstTarget": "first", "secondTarget": "second",
            "first": {"before": 11, "amountBits": 0x40400000, "after": 29},
            "second": {"before": 37, "amountBits": 0x40800000, "after": 47}}


def _record(kind, receiver, incoming, state, stage):
    before = deepcopy(state)
    target = "null" if receiver == "null" else state[receiver + "Target"]
    exception = "System.NullReferenceException" if target == "null" else "none"
    transported = _argument_bits(incoming, 32, stage)
    if exception == "none": state[target]["amountBits"] = transported
    return {"kind": kind, "receiver": receiver, "incoming": incoming, "exception": exception,
            "transportedIncomingBits": transported, "before": before, "after": deepcopy(state)}


def observations(stage="player"):
    prefix = ASSEMBLY + "."
    fields = {"Cell.Before": "System.Byte", "Cell.Amount": "System.Single", "Cell.After": "System.Byte",
              "Holder.Target": prefix + "Cell"}
    rows = [{"kind": "declarations", "methods": 3, "fields": 4, "properties": 0, "types": 3,
             "signatures": {"Holder.StoreSingle": ["System.Void", "System.Single"]}, "fieldTypes": fields,
             "fieldAccess": {name: "Public" for name in fields},
             "enclosing": {"Cell": "none", "Container": "none", "Holder": prefix + "Container"},
             "visibility": {"Cell": 1, "Container": 1, "Holder": 2}},
            {"kind": "defaults", "targetNull": True, "cell": {"before": 0, "amountBits": 0, "after": 0}}]
    for index, incoming in enumerate(VALUES["single"]):
        for mode in ("value", "null-target", "null-owner"):
            state = _fresh()
            if mode == "null-target": state["firstTarget"] = "null"
            rows.append(_record(mode + "-" + str(index), "null" if mode == "null-owner" else "first", incoming, state, stage))
    state = _fresh()
    rows.append(_record("reuse-first", "first", 0x80000000, state, stage))
    rows.append(_record("reuse-second", "second", 1, state, stage))
    state["firstTarget"] = "second"
    rows.append(_record("retarget-first", "first", 0x7fc00001, state, stage))
    rows.append(_record("shared-second", "second", 0xff800000, state, stage))
    state["firstTarget"] = "null"
    rows.append(_record("reuse-null-target", "first", 0x3fc00000, state, stage))
    state["firstTarget"] = "first"
    rows.append(_record("restore-first", "first", 0x7f800002, state, stage))
    rows.append(_record("reuse-null-owner", "null", 0xffc00123, state, stage))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, PROFILE, observations(stage), 3,
                         "All three methods and the original enclosing declaration; bit-preserving stores, "
                         "null failures before effects, fresh captures, aliases, replacement targets and unchanged neighbors; "
                         "independently observed editor binary32 argument quieting and native preservation, "
                         "no altered floating-control/status claim")
