"""Independent exhaustive low16 and signed-state oracle for a two-byte value wrapper."""

from behavior_oracle import verify_report


BOUNDARIES = (0, 1, 2, 0x7e, 0x7f, 0x80, 0xff, 0x100, 0x101, 0x7ffe, 0x7fff,
              0x8000, 0x8001, 0x80ff, 0xff00, 0xff7f, 0xff80, 0xfffe, 0xffff, 0x1234, 0xabcd)


def signed_word(bits):
    return (bits & 0x7fff) - (bits & 0x8000)


def seed(kind):
    return {"kind": kind, "value": -17, "bits": 65519, "unwrapped": -17, "after": -17}


def observations():
    type_name = "ScalarWordWrapperConversionFixture.WordCell"
    rows = [
        {"kind": "declarations", "methods": 3, "fields": 2, "valueType": True, "sequentialLayout": True,
         "size": 2, "beforeFieldInit": True, "hasTypeInitializer": True, "valueFieldType": "System.Int16",
         "seedFieldType": type_name, "seedIsStatic": True, "seedIsReadOnly": True,
         "signatures": sorted(("op_Explicit:System.Int16->" + type_name,
                               "op_Explicit:" + type_name + "->System.Int16"))},
        {"kind": "default", "value": 0, "unwrapped": 0, "after": 0}, seed("seed-before"),
    ]
    for start in range(0, 65536, 256):
        # Fixed four-digit records are lossless, ordered observations of every
        # low16 result. They keep reports small without replacing values by a hash.
        results = "".join(f"{bits:04x}" for bits in range(start, start + 256))
        for operation in ("wrap", "unwrap"):
            rows.append({"kind": "exhaustive", "operation": operation, "startBits": start, "count": 256,
                         "resultBits": results, "inputsUnchanged": True, "seedValue": -17})
    for bits in BOUNDARIES:
        value = signed_word(bits)
        rows.append({"kind": "boundary-alias", "bits": bits, "input": value, "constructedValue": value,
                     "unwrappedValue": value, "slotResult": value, "slotValue": value, "originalAfter": value,
                     "copyValue": 31, "sameArray": True, "neighborValue": -17, "seedValue": -17})
    rows.append(seed("seed-after"))
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "scalar-word-wrapper-conversion", observations(), 3,
                         "both conversion directions over all 65536 low16 values with lossless ordered results, "
                         "signed boundaries, unchanged by-value copies/ref-array aliases and readonly cctor sentinel; "
                         "beforefieldinit metadata observed, precise early initialization timing remains runtime-permitted")
