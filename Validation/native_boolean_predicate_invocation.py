"""Independent ordered state oracle for captured and live Boolean arguments."""

from behavior_oracle import verify_report


PROFILE = "native-boolean-predicate-invocation"
NODE = "NativeBooleanPredicateInvocationFixture.Node"
OPERATIONS = ("negated", "stored-pair", "live-pair", "complement-live")
KINDS = ("success", "first-null", "second-null", "owner-null", "self-first", "shared-target", "self-second")


def state(calls=0, flag=False, first=None, second=None):
    return {"calls": calls, "flag": flag, "first": first, "second": second}


def record(operation, kind, initial, nodes):
    owner = nodes["owner"]
    failure = owner is None
    if owner is not None:
        if operation == "stored-pair":
            owner["flag"] = not initial
        first_argument = not owner["flag"] if operation in ("negated", "complement-live") else owner["flag"]
        target = nodes.get(owner["first"])
        if target is None:
            failure = True
        else:
            target["calls"] += 1
            target["flag"] = first_argument
            if operation != "negated":
                second_argument = owner["flag"] if operation == "complement-live" else not owner["flag"]
                target = nodes.get(owner["second"])
                if target is None:
                    failure = True
                else:
                    target["calls"] += 1
                    target["flag"] = second_argument
    return {"kind": kind, "operation": operation, "initialFlag": initial, "inputFlag": not initial,
            "exception": "System.NullReferenceException" if failure else "none",
            **{name: None if value is None else value.copy() for name, value in nodes.items()}}


def nodes(initial):
    return {"owner": state(3, initial, "first", "second"),
            "first": state(7, not initial), "second": state(11, initial)}


def observations():
    rows = [{"kind": "declarations", "methods": 6, "fields": 4, "parameterType": "System.Boolean",
             "storedParameterType": "System.Boolean", "negatedParameters": 0, "liveParameters": 0,
             "complementParameters": 0, "flagType": "System.Boolean", "firstType": NODE, "secondType": NODE},
            {"kind": "defaults", "node": state()}]
    for operation in OPERATIONS:
        for initial in (False, True):
            for kind in KINDS:
                current = nodes(initial)
                if kind == "first-null": current["owner"]["first"] = None
                if kind == "second-null": current["owner"]["second"] = None
                if kind == "owner-null": current["owner"] = None
                if kind == "self-first": current["owner"]["first"] = "owner"
                if kind == "shared-target": current["owner"]["second"] = "first"
                if kind == "self-second": current["owner"]["second"] = "owner"
                rows.append(record(operation, kind, initial, current))
            current = nodes(initial)
            current["owner"]["first"] = None
            rows.append(record(operation, "reuse-failure", initial, current))
            current["owner"]["first"] = "first"
            rows.append(record(operation, "reuse-success", initial, current))
            rows.append(record(operation, "repeat", initial, current))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, PROFILE, observations(), 6,
                         "exact Boolean field captures and live reads between ordered checked calls; "
                         "owner store before failure, both target-null orders, self/target aliases, reuse and repeat; "
                         "all declaration and recorded state identities")
