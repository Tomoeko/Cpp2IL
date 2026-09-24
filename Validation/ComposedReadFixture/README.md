# Composed reference-array read control

`ArrayReader.ReadTwice` reads two elements through a nested `object[]` field.
An observable counter increment and no-inline marker call occur between the
reads. The harness checks null failures before the first array access, bounds
failures at both access positions, reference equality, unchanged array contents,
effect preservation, and signed counter and marker wraparound. The native and
final-graph proofs check the counter-store and marker-call order. The runtime
oracle compares exception types and visible state; its independent increments
do not distinguish their relative order. Messages, stack traces, concurrency,
and authored local names are outside the oracle.

This is a positive-only control. The separate `ComposedArrayFixture` retains
its array-replacement method and negative alias-change baseline unchanged.
