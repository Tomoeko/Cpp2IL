"""Independent bit arithmetic for by-value small aggregate field reads."""

from behavior_oracle import verify_report


INT16_PATTERNS = (0, 1, 2, 0x7e, 0x7f, 0x80, 0xff, 0x100, 0x101, 0x7ffe, 0x7fff,
                  0x8000, 0x8001, 0x80ff, 0xff00, 0xff7f, 0xff80, 0xfffe, 0xffff, 0x1234, 0xabcd)


def _signed(bits, width):
    boundary = 1 << (width - 1)
    return (bits + boundary) % (1 << width) - boundary


def observations():
    expected = []
    for bits in range(256):
        for operation, value in (("readSByte", _signed(bits, 8)), ("widenSByte", _signed(bits, 8)),
                                 ("readByte", bits), ("widenByte", bits)):
            expected.append({"operation": operation, "bits": bits, "before": value,
                             "result": value, "after": value, "failure": "none"})
    for bits in INT16_PATTERNS:
        for operation, value in (("readInt16", _signed(bits, 16)), ("widenInt16", _signed(bits, 16)),
                                 ("readUInt16", bits), ("widenUInt16", bits)):
            expected.append({"operation": operation, "bits": bits, "before": value,
                             "result": value, "after": value, "failure": "none"})
    return expected


def verify(path, stage, version):
    return verify_report(path, stage, version, "small-aggregate-getter", observations(), 8,
                         "Eight by-value one-field sequential aggregate reads; exhaustive byte patterns, "
                         "signed/unsigned 16-bit boundaries, same-width and 32-bit results, unchanged inputs")
