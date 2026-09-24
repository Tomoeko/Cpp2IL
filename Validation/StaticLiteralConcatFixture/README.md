# Static literal concatenation control

The selected assembly has one explicit method: a static string parameter joined
to a metadata-backed literal. A neighboring static field makes unexpected
writes observable without introducing another method. `NoInlining` keeps the
method available as a separate recovery target in the exact Windows x64 Release
IL2CPP player.

The independent behavior oracle covers null and empty inputs, repeated calls
with the same input reference, Unicode, a suffix-containing input, result
content and reference identity, and the neighboring field remaining unchanged.
The fixture isolates the guarded StringLiteral initializer and
`String.Concat(string, string)` tail from the separate class-cast and inherited
field controls. Exact player-only recovery and Unity round-trip acceptance are
now established for this bounded method: strict recovery emits 1/1 selected
methods, pinned ILVerify and both declaration comparisons pass, and generated
source compiles and rebuilds under the supplied Windows Unity 2021.3.35f1
Release IL2CPP target. Original and recovered editor/player runs each pass all
six observations. The local accepted receipt is under
`Files/validation/static-literal-concat-roundtrip-01/roundtrip.json`.
