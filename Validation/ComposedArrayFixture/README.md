# Composed reference-array control

`ArrayReader.ReadTwice` reads two elements from a nested `object[]` field with
an observable counter store and a no-inline marker call between the accesses.
Both element references contribute to the result. The separate
`ReadAcrossReplacement` method calls a no-inline helper that replaces the
array field before the second read. It checks that a recovery rule does not
reuse the first array reference when the field can change across a call.

The independent `ComposedArrayHarness` and `composed_array.py` oracle check
first- versus second-access null and bounds failures, reference equality,
unchanged array contents, field and call effects, array identity changes, and
signed counter wraparound. The supplied Windows Unity 2021.3.35f1 editor
compiled the original source and built a Windows x64 Release IL2CPP player
with zero errors and four Unity warnings. Original editor and player runs each
passed all 264 observations. Metadata confirms six selected method identities,
including two constructors and both helpers. A strict player-only diagnostic
emitted the constructors and `Mark`, but rejected `ReplaceValues` at a null
guard and both read methods at unresolved bounds-helper semantics. The three
emissions have no recovered-source Unity or behavioral acceptance.

This assembly contains both the positive method and the alias-change control.
An initial compositional proof may correctly leave the latter unresolved; it
must not be counted as a strict assembly-wide success in that case. Exception
types and visible state are checked; messages, stack traces, concurrency, and
authored local names are outside the behavior oracle.
