# Three-field scalar-float reference mutation fixture

`FloatAcrossCall.Identity` clears the three `Single` fields of a by-reference
`FloatPack` and returns its separate `Single` argument. `Sum` saves those three
fields from a by-value struct, calls `Identity` on its local copy, then adds the
saved values to the returned value in field order. The fixture has two
application methods and is separate from the accepted four-field XMM spill
fixture.

The independent harness calls both methods. It records input and output bits,
the three direct by-reference mutations, and the unchanged caller-owned struct
after the by-value call. Vectors include finite values, both signs of zero,
subnormals, infinities, and quiet NaNs. The Python oracle checks exact bits for
non-NaN outputs and classifies NaN outputs without assuming a payload. These
checks use separate Windows editor and Release player expectations for two sums
with distinct exact-bit results. The editor results are consistent with extra
intermediate precision; no particular JIT mechanism is assumed. The oracle keeps
the two stages distinct and compares original and recovered observations in
each stage. These observations do not establish floating-point status flags,
signaling-NaN behavior, other rounding modes, or all possible operands.

The supplied Unity 2021.3.35f1 Windows editor confirms the original native
shape and the full player-only round trip. Both selected methods pass strict
recovery and typed IL verification; both declaration comparisons have zero
differences against the stripped managed oracle. Original and recovered source
compile in the exact editor and rebuild as Windows x64 Release IL2CPP players.
All four editor/player runs pass the 13-vector oracle. Use
`Validation/run_fixture.py --profile xmm-ref-mutation --stage run` to build the
original, then
`Validation/run_roundtrip.py --profile xmm-ref-mutation` for recovery, typed IL,
declaration comparisons, fresh Unity compilation, native rebuild, and original
and recovered editor/player behavior. Keep generated material under ignored
`Files/`.
