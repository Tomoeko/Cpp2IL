"""Independent bit arithmetic for narrow enum parameter conversions; recovery uses no oracle."""

from behavior_oracle import verify_report


def word_bits():
    result = list(dict.fromkeys((0, 1, 2, 127, 128, 255, 256, 32766, 32767, 32768, 32769, 65534, 65535)))
    for bit in range(16):
        for value in (1 << bit, 65535 ^ (1 << bit)):
            if value not in result:
                result.append(value)
    return result


def observations():
    rows = []
    for width, signed_name, unsigned_name, patterns in ((8, "sbyte", "byte", range(256)),
                                                       (16, "int16", "uint16", word_bits())):
        for bits in patterns:
            signed = bits - (1 << width) if bits >= (1 << (width - 1)) else bits
            for name, value in ((signed_name, signed), (unsigned_name, bits)):
                for target in ("i4", "u4"):
                    rows.append({"route": name + "-" + target, "bits": bits, "input": value,
                                 "inputAfter": value, "result": value if target == "i4" else value % (1 << 32),
                                 "failure": "none"})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "enum-integer-conversion", observations(), 8,
                         "Eight signed/unsigned byte/word enum parameters to Int32/UInt32; all256 byte values, "
                         "word sign/bit boundaries, undefined enum values and unchanged original parameter values")
