"""Independent identity and null-failure oracle for an ordinary class property."""

from behavior_oracle import verify_report


CHECKS = (
    "distinct-cells", "distinct-payloads", "first-identity", "first-marker",
    "payload-neighbors-unchanged", "other-cell-null", "alias-replacement", "alias-readback",
    "replacement-marker", "old-payload-unchanged", "second-identity", "first-cell-unchanged",
    "second-marker", "repeat-identity", "repeat-markers", "clear-is-null", "clear-second-unchanged",
    "clear-payloads-unchanged", "shared-first", "shared-second", "shared-marker-unchanged",
    "cleared-first-does-not-clear-second",
)
COLLECTION_CHECKS = (
    "collection-retains-payload", "collection-retains-identity", "collection-retains-marker",
    "collection-preserves-neighbors",
)


def observations():
    payload = "ClassReferenceSetterFixture.Payload"
    rows = [{"kind": "declarations", "methods": 4, "fields": 2, "properties": 1,
             "propertyType": payload, "fieldType": payload, "fieldPrivate": True,
             "getter": True, "setter": True},
            {"kind": "defaults", "firstNull": True, "secondNull": True, "marker": 0}]
    rows.extend({"kind": "check", "check": check, "result": True} for check in CHECKS)
    rows.extend({"kind": "exception", "check": check, "exception": "System.NullReferenceException"}
                for check in ("null-owner-value", "null-owner-null", "null-owner-getter"))
    rows.extend({"kind": "check", "check": check, "result": True} for check in COLLECTION_CHECKS)
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "class-reference-setter", observations(), 4,
                         "Original class-typed property and private reference storage, null receivers, "
                         "assignment identity, aliases, repeated writes, unchanged payload markers and "
                         "bounded collection survival; no complete collector or concurrent-write claim")
