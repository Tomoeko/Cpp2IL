# Scalar truncation fixture

Two separately preserved methods convert one `double` parameter to a signed
`Int32` or `Int64` with an unchecked cast. The fixture has no static data or helper
methods. The independent harness constructs 66 exact binary64 bit patterns from
hexadecimal input, then records 132 result bit patterns as hexadecimal strings.
This avoids decimal parsing, JSON integer precision and signed-zero normalization.

Inputs cover both zeros, subnormals, fractions, representable neighbors of the
Int32 and Int64 limits, large finite values, infinities and quiet NaNs of both
signs. The Python oracle truncates finite inputs with `math.trunc`, then checks
the resulting arbitrary-precision integer against the signed destination range.
For invalid conversions it predicts the signed minimum bit pattern, following
the masked-invalid result of `CVTTSD2SI` in
[Intel's instruction reference, Volume 2A, CVTTSD2SI](https://cdrdv2-public.intel.com/812383/253666-sdm-vol-2a.pdf).

That invalid-result rule is a **candidate Windows x64 contract**, not a portable
guarantee of an unchecked C# cast. The original supplied Unity 2021.3.35f1 Windows
editor and Release IL2CPP player must each produce every expected observation
before it can support a recovery acceptance claim. Any editor/player difference,
exception, missing result or saturation result is a real scope failure; do not
normalize or rewrite observations to make the stages agree. The verifier rejects
other editor/player platforms. It checks output bits, not MXCSR status flags or
behavior with externally modified floating-point exception/control state.

The `scalar-truncation` runner profile passes all 132 result checks in both the
supplied Windows editor and its original Release IL2CPP player. Player-only native
inspection confirms two methods, each containing one register `CVTTSD2SI` and a
return. These original-build observations do not establish recovered behavior.
The recovered scope also passes strict player-only recovery of both methods,
typed IL verification, both declaration comparisons with zero differences,
exact-editor compilation and behavior, and a rebuilt native player with all 132
matching result checks. Generated code guards NaN and out-of-range inputs before
performing a managed conversion; it does not rely on unspecified invalid casts.
Packed conversions, single-precision inputs, unsigned results, memory operands,
checked casts and signaling NaNs remain separate scopes.
