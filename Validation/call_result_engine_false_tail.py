"""Independent observations for an engine call-result guarded Boolean tail."""

from behavior_oracle import verify_report


def observations():
    return [
        {"kind": "declarations", "componentClass": True,
         "oneMethod": True, "oneConstructor": True,
         "disableSignature": True},
        {"kind": "active", "before": True, "after": False,
         "exception": "none"},
        {"kind": "repeat", "before": False, "after": False,
         "exception": "none"},
        {"kind": "initially-inactive", "before": False, "after": False,
         "exception": "none"},
    ]


def verify(path, stage, version):
    return verify_report(path, stage, version, "call-result-engine-false-tail",
                         observations(), 2,
                         "engine getter result and false Boolean tail call on active and inactive objects")
