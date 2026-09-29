"""Independent arithmetic and ordered alias effects for complete byref splits."""

from behavior_oracle import verify_report


LOW_WORDS = (0, 1, 2, 0x7f, 0x80, 0x7fff, 0x8000, 0x7ffffffe, 0x7fffffff,
             0x80000000, 0xfffffffe, 0xffffffff)
HIGH_WORDS = (0, 1, 0x7fffffff, 0x80000000, 0xffffffff, 0x12345678, 0x80000001, 0xffff0000)
ROUTES = ("static-signed", "instance-signed", "static-unsigned", "instance-unsigned",
          "static-unsigned-to-signed", "instance-unsigned-to-signed")


def _signed32(value):
    return (value + (1 << 31)) % (1 << 32) - (1 << 31)


def _state(kind, route, slot0, slot1, after0, after1, guard0, guard1, initial=None):
    receiver = initial is not None and route.startswith("instance-")
    neighbor = (-137, 0, 911)[initial] if receiver else None
    return {
        "kind": kind, "route": route, "failure": "none" if kind == "split" else "System.NullReferenceException",
        "slot0Before": slot0, "slot1Before": slot1, "slot0After": after0, "slot1After": after1,
        "guard0Before": guard0, "guard1Before": guard1, "guard0After": guard0, "guard1After": guard1,
        "storageSame": True, "receiverNeighborBefore": neighbor, "receiverNeighborAfter": neighbor,
        "receiverReferenceNullBefore": initial == 0 if receiver else None,
        "receiverReferenceSame": True if receiver else None,
    }


def observations():
    expected = [{"kind": "constructor", "neighbor": 0, "referenceNull": True}]
    for high in HIGH_WORDS:
        for low in LOW_WORDS:
            bits = (high << 32) | low
            for initial in range(3):
                for alias in (False, True):
                    for route in ROUTES:
                        unsigned = route in ("static-unsigned", "instance-unsigned")
                        slot0 = (0, 0x80000000, 0xffffffff)[initial] if unsigned else (-(1 << 31), 0, (1 << 31) - 1)[initial]
                        slot1 = slot0 ^ 0xffffffff if unsigned else ~slot0
                        guard0, guard1 = (0x01234567, 0xfedcba98) if unsigned else (-(1 << 31) + 73, (1 << 31) - 1 - 79)
                        result_low = low if unsigned else _signed32(low)
                        result_high = high if unsigned else _signed32(high)
                        row = _state("split", route, slot0, slot1, result_high if alias else result_low,
                                     slot1 if alias else result_high, guard0, guard1, initial)
                        row.update(bits=bits, initial=initial, alias=alias)
                        expected.append(row)
    for route in ("instance-signed", "instance-unsigned", "instance-unsigned-to-signed"):
        unsigned = route == "instance-unsigned"
        guard0, guard1 = (0x01234567, 0xfedcba98) if unsigned else (-(1 << 31) + 73, (1 << 31) - 1 - 79)
        slot0 = 17 if unsigned else -17
        expected.append(_state("null", route, slot0, 29, slot0, 29, guard0, guard1))
    return expected


def verify(path, stage, version):
    return verify_report(path, stage, version, "byref-integer-halves", observations(), 7,
                         "Six static/instance signed-ref, unsigned-ref and unsigned-to-signed-out splits plus constructor; "
                         "96 full64 patterns, ordered same-destination aliases, unchanged guards/receiver state and null failures")
