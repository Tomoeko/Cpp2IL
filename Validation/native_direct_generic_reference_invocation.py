"""Independent oracle for a shared generic reference body and two concrete calls."""

from behavior_oracle import verify_report


PROFILE = "native-direct-generic-reference-invocation"
ASSEMBLY = "NativeDirectGenericReferenceInvocationFixture"
ROUTES = ("echo-first", "echo-second", "wrapper-first", "wrapper-second")
FRESH_CASES = ("receiver-null", "value-null", "value-a", "value-b", "negative", "overflow")
NULL_FAILURE = "System.NullReferenceException"


def _record(kind, route, receiver, argument, calls):
    before = calls.copy()
    failure = NULL_FAILURE if receiver == "null" else "none"
    if receiver != "null":
        calls[receiver] = ((calls[receiver] + 1 + (1 << 31)) & 0xffffffff) - (1 << 31)
    return {"kind": kind, "route": route, "receiver": receiver, "argument": argument,
            "result": "not-returned" if failure != "none" else argument, "exception": failure,
            "before": before, "after": calls.copy()}


def observations():
    prefix = ASSEMBLY + "."
    rows = [{"kind": "declarations", "methods": 6, "fields": 1, "types": 3, "properties": 0,
             "signatures": {"Relay.Echo": ["T", "T"],
                            "Relay.FirstCall": [prefix + "First", prefix + "First"],
                            "Relay.SecondCall": [prefix + "Second", prefix + "Second"]},
             "genericArity": {"Relay.Echo": 1, "Relay.FirstCall": 0, "Relay.SecondCall": 0},
             "genericParameters": {"Relay.Echo": [{"name": "T", "position": 0,
                                                    "attributes": "ReferenceTypeConstraint", "constraints": []}]},
             "fieldTypes": {"Relay.Calls": "System.Int32"}, "fieldAccess": {"Relay.Calls": "Public"}},
            {"kind": "defaults", "calls": 0, "firstCreated": True, "secondCreated": True}]
    for route in ROUTES:
        element = "first" if route.endswith("first") else "second"
        for case in FRESH_CASES:
            calls = {"shared": 0, "other": -13, "overflow": (1 << 31) - 1}
            receiver = "null" if case == "receiver-null" else "shared"
            argument = "null" if case == "value-null" else element + ("-b" if case == "value-b" else "-a")
            if case == "negative": calls["shared"] = -2
            if case == "overflow": calls["shared"] = (1 << 31) - 1
            rows.append(_record(route + "-" + case, route, receiver, argument, calls))
    calls = {"shared": 0, "other": 0, "overflow": (1 << 31) - 1}
    for kind, route, receiver, argument in (
            ("alternating-first", "wrapper-first", "shared", "first-a"),
            ("alternating-second", "wrapper-second", "shared", "second-a"),
            ("alternating-first-null", "wrapper-first", "shared", "null"),
            ("alternating-second-null", "wrapper-second", "shared", "null"),
            ("repeat-first-a", "wrapper-first", "shared", "first-a"),
            ("repeat-second-a", "wrapper-second", "shared", "second-a"),
            ("changed-first-b", "wrapper-first", "shared", "first-b"),
            ("changed-second-b", "wrapper-second", "shared", "second-b"),
            ("direct-after-wrappers-first", "echo-first", "shared", "first-a"),
            ("direct-after-wrappers-second", "echo-second", "shared", "second-b"),
            ("independent-first-shared-input", "wrapper-first", "other", "first-a"),
            ("original-receiver-preserved", "wrapper-first", "shared", "first-a"),
            ("cross-wrapper-overflow-first", "wrapper-first", "overflow", "first-b"),
            ("cross-wrapper-overflow-second", "wrapper-second", "overflow", "second-b")):
        rows.append(_record(kind, route, receiver, argument, calls))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, PROFILE, observations(), 6,
                         "Six original methods including Echo<T>; both closed generic reference calls, "
                         "null values and receivers, return identity, real side effects, Int32 wrap, "
                         "alternating/repeated calls and independent receivers")
