"""Independent typed behavior oracle for a nested unsigned byte field read."""

from behavior_oracle import verify_report


def observations():
    rows = [
        {"kind": "default-cell", "value": 0, "neighbor": 0,
         "referenceNull": True},
        {"kind": "default-owner", "childNull": True,
         "referenceNull": True, "neighbor": 0},
        {"kind": "null-owner", "failure": "System.NullReferenceException"},
        {"kind": "null-child", "failure": "System.NullReferenceException",
         "childNull": True, "referenceSame": True, "neighbor": 913},
    ]
    for value in range(256):
        rows.append({
            "kind": "value", "input": value, "failure": "none",
            "first": value, "second": value, "aliasResult": value,
            "cellValue": value, "cellNeighbor": -701,
            "cellReferenceSame": True, "ownerChildSame": True,
            "ownerReferenceSame": True, "ownerNeighbor": 913,
            "aliasChildSame": True, "aliasReferenceSame": True,
            "aliasNeighbor": -31,
        })
    return rows


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "nested-byte-field-read", observations(), 3,
        "A nullable nested reference field read widened from unsigned byte to Int32; "
        "constructor defaults, null failures, all byte values, repeated reads, "
        "shared-child aliases and unchanged neighboring fields and references")
