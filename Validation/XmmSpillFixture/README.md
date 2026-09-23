# Nonvolatile XMM spill fixture

`FloatAcrossCall.Sum` loads three `Single` fields from a by-value `FloatPack`
argument, keeps them live across a no-inline managed call to
`FloatPack.ClearAndReadFirst`, then adds them to the returned first value. The
callee clears a field in the method's local struct copy, so the pre-call field
values cannot be loaded afterward. The fixture has
two application methods. A separate Unity harness records input and result
bits for five finite vectors; an independent Python oracle checks those bits.
No original managed assembly or generated C++ is supplied to recovery.

The supplied Unity 2021.3.35f1 Windows x64 Release IL2CPP build places the three
live values in XMM6–XMM8. Its native method saves those nonvolatile registers
with aligned 16-byte stack moves, has corresponding unwind entries, makes one
direct managed call, and restores the same registers before returning. The
values enter XMM6–XMM8 through scalar `movss` loads, leaving the six stack
`movaps` instructions as the only packed moves in this method. This
gives a bounded positive case for treating matched ABI preservation as
nonsemantic. It does not justify discarding other packed SIMD operations,
unmatched saves, overlapping stack writes, or values that escape the frame.

Use the exact supplied Windows editor and toolchain through
`Validation/run_fixture.py --profile xmm-spill --stage run` for the original
build. `Validation/run_roundtrip.py --profile xmm-spill` checks player-only
recovery, typed IL, declarations, fresh Unity compilation and IL2CPP rebuild,
and editor/player behavior of generated source. Keep all resulting projects,
binaries and raw receipts under the ignored `Files/` directory.
