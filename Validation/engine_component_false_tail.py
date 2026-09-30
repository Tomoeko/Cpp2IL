"""Independent observations for two guarded Unity engine calls."""

from behavior_oracle import verify_report


def observations():
    return [
        {"kind": "declarations", "staticClass": True, "oneMethod": True,
         "noConstructor": True, "disableSignature": True},
        {"kind": "active", "before": True, "after": False,
         "exception": "none"},
        {"kind": "repeat", "before": False, "after": False,
         "exception": "none"},
        {"kind": "initially-inactive", "before": False, "after": False,
         "exception": "none"},
        {"kind": "null-component", "before": True, "after": True,
         "exception": "System.NullReferenceException"},
    ]


def verify(path, stage, version):
    return verify_report(path, stage, version, "engine-component-false-tail",
                         observations(), 1,
                         "engine getter and false Boolean tail call, including a null component")
