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
pending.
