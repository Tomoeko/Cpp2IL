"""Independent observations for reference fields read through an instance argument."""

from behavior_oracle import verify_report


def observations():
    return [
        {"kind": "filled", "neighbor": 91, "neighborAfter": 91,
         "textFailure": "none", "textSame": True, "textValue": "ttt",
         "payloadFailure": "none", "payloadSame": True,
         "numbersFailure": "none", "numbersSame": True,
         "numbersLength": 3, "numbersFirst": -(1 << 31)},
        {"kind": "empty", "neighbor": 91, "neighborAfter": 91,
         "textFailure": "none", "textSame": True, "textValue": "",
         "payloadFailure": "none", "payloadSame": True,
         "numbersFailure": "none", "numbersSame": True,
         "numbersLength": 0, "numbersFirst": None},
        {"kind": "null-values", "neighbor": 91, "neighborAfter": 91,
         "textFailure": "none", "textSame": True, "textValue": None,
         "payloadFailure": "none", "payloadSame": True,
         "numbersFailure": "none", "numbersSame": True,
         "numbersLength": None, "numbersFirst": None},
        {"kind": "null-box", "neighbor": None, "neighborAfter": None,
         "textFailure": "NullReferenceException", "textSame": None, "textValue": None,
         "payloadFailure": "NullReferenceException", "payloadSame": None,
         "numbersFailure": "NullReferenceException", "numbersSame": None,
         "numbersLength": None, "numbersFirst": None},
        {"kind": "null-reader", "neighbor": 91, "neighborAfter": 91,
         "textFailure": "NullReferenceException", "textSame": None, "textValue": None,
         "payloadFailure": "NullReferenceException", "payloadSame": None,
         "numbersFailure": "NullReferenceException", "numbersSame": None,
         "numbersLength": None, "numbersFirst": None},
    ]


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "instance-parameter-reference-read",
        observations(), 5,
        "Three instance reference-field reads through a distinct class argument; "
        "field value and identity, null argument failures, and unchanged neighbor")
