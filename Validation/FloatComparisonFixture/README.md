# Scalar floating comparison fixture

Twelve methods independently expose equality, inequality and the four ordered
relations for Single and Double. The separate driver constructs operands from
explicit IEEE bit patterns and records their hexadecimal input identities and
six Boolean results. JSON never has to encode NaN, infinity or negative zero.

The finite vectors include both zeros, positive/negative one, smallest normal
and subnormal values, finite maxima, infinities and quiet NaNs with different
signs/payloads. An independent Python oracle checks every operand pair and
requires exact Boolean types. Original and recovered players must both match;
fixture existence alone does not prove recovery or compilation.

Unordered comparisons must retain the native ZF/PF/CF relationship. In
particular, a negated ordered relation is not the opposite ordered relation
when either operand is NaN. The instruction contracts are documented in
[Intel's UCOMISD/UCOMISS reference](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2b-manual.pdf)
and [ECMA-335, Partition III](https://ecma-international.org/publications-and-standards/standards/ecma-335/).

Signaling-NaN exception status, externally changed floating-point control state,
packed SIMD operations and MIN/MAX selection are separate scopes. This fixture
does not establish them or byte-identical floating-point execution.

The exact Unity 2021.3.35f1 Windows x64 Release round trip passes all twelve
player-only recovered methods through strict analysis, typed IL verification and
both declaration comparisons with zero differences. The original and rebuilt
players each match all 2,028 Boolean results across 338 input pairs; both fresh
editor runs also pass. Generated source is never repaired by hand. The tested
scalar COMISS/COMISD/UCOMISS/UCOMISD register forms preserve the unordered parity
flag and the parity branches used by equality and inequality. Native memory
operands still require a separate read-width proof.
