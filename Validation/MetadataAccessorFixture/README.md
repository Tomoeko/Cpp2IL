# Inline-eligible static accessor experiment

This neutral fixture calls an observable, non-inlined `Prepare()` method before
passing `Accessor.ReadCurrent()` to one of two distinct non-inlined receivers.
The small accessor reads another type's static `object` field and is marked
`AggressiveInlining`. None of these types has a static constructor or field
initializer.

The intended runner profile is `metadata-accessor`, assembly
`MetadataAccessorFixture`, with six selected methods: `Prepare`, `ReadCurrent`,
the two `Receive` overloads, and the two `Forward` overloads. Its separate
harness checks eleven declaration, cold, repeated, mutation, null,
signed-argument and exception observations.

## Exact original experiment

A fresh Unity 2021.3.35f1 Windows editor run compiled this source, built a
Windows x64 Release IL2CPP player with zero errors, and passed all eleven
observations in both editor and player. IL2CPP generated an inline-eligible
accessor with its own TypeInfo guard. Both generated forwarding bodies call
`Prepare()` before that accessor. The native compiler inlined the accessor:
the two forwarding methods are complete file-backed, handler-free roots of
fourteen and eighteen instructions, with `Prepare()` before the TypeInfo guard,
static-field read and direct tail jump.

The native helper in this exact build satisfies the existing metadata-helper
proof. An alternate helper variant observed in other inputs remains unproved,
so the matching wrapper order is only a partial shape match. A player-only
strict diagnostic completed analysis but rejected all six selected methods.
The accessor and both forwarding methods had unresolved metadata-initialization
effects; `Prepare()` had an unsupported native exception-region boundary; the
two receivers had unresolved native flag values and narrow integer operations.
No recovered source compilation or rebuild was attempted for this experiment.

Inlining is a compiler decision, and the generated source does not reveal the
original managed structure of another player. Guard order and runtime-helper
effects must be proved for each accepted native shape.
