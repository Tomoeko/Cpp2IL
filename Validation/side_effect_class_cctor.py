"""Independent behavior oracle for a side-effectful class constructor."""

from behavior_oracle import verify_report


def observations():
    return [
        {"kind": "before", "events": 0,
         "type": "SideEffectClassCctorFixture.StaticCells"},
        {"kind": "first", "events": 1, "marker": 37, "bias": "80000000"},
        {"kind": "repeat", "events": 1, "marker": 73, "bias": "80000000"},
    ]


def verify(path, stage, version):
    return verify_report(path, stage, version, "side-effect-class-cctor",
                         observations(), 1,
                         "Class initialization runs once, increments an external "
                         "witness, and stores distinct Int32 and negative-zero fields")
