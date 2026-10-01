"""Shared original-only oracle for explicit parent, owner or element initialization."""

from behavior_oracle import verify_report
from framework_ancestor_array_argument import ASSEMBLY as FRAMEWORK_ASSEMBLY
from framework_ancestor_array_argument import observations as framework_observations


def _rename(value, assembly):
    if isinstance(value, dict): return {key: _rename(item, assembly) for key, item in value.items()}
    if isinstance(value, list): return [_rename(item, assembly) for item in value]
    return value.replace(FRAMEWORK_ASSEMBLY, assembly) if isinstance(value, str) else value


def observations_for(assembly, kind, methods, types):
    rows = _rename(framework_observations(), assembly)
    declarations = rows[0]
    declarations.update(methods=methods, fields=4, types=types)
    declarations["fieldTypes"]["Probe.Events"] = "System.Int32"
    declarations["fieldAccess"]["Probe.Events"] = "Public"
    declarations["baseTypes"]["Probe"] = "System.Object"
    declarations["fieldStatic"] = {"Cell.Items": False, "Cell.Calls": False, "Cell.Value": False, "Probe.Events": True}
    if kind == "parent":
        declarations["baseTypes"]["Parent"] = "System.Object"
        declarations["baseTypes"]["Cell"] = assembly + ".Parent"
    payload_events = 1 if kind == "element" else 0
    defaults = dict(rows[1], events=1)
    lifecycle = [declarations,
                 {"kind": "probe-before-creation", "events": 0},
                 {"kind": "null-before-creation", "eventsBefore": 0, "eventsAfter": 0,
                  "exception": "System.NullReferenceException"},
                 {"kind": "payload-first-created", "events": payload_events, "created": True},
                 {"kind": "payload-second-created", "events": payload_events, "created": True},
                 {"kind": "cell-first-created", "events": 1, "created": True},
                 {"kind": "cell-second-created", "events": 1, "created": True}, defaults,
                 {"kind": "payload-repeat-created", "events": 1, "created": True},
                 {"kind": "cell-repeat-created", "events": 1, "created": True}]
    return lifecycle + [dict(row, eventsBefore=1, eventsAfter=1) for row in rows[2:]]


def verify_for(path, stage, version, profile, rows):
    return verify_report(path, stage, version, profile, rows, rows[0]["methods"],
                         "Original-only explicit static initialization: fresh null receiver before creation, "
                         "exact owner/parent/element lifecycle, counter runs once, checked array faults, "
                         "identity and repeated calls; strict recovered rejection required separately")
