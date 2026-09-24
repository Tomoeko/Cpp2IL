# Static scalar setter fixture

This neutral Unity 2021.3.35f1 Windows x64 Release IL2CPP control isolates
one ordinary `void` method that writes its unchanged `int` argument to an
`int` static field on its own nongeneric class. The class has no managed
static constructor. Two adjacent fields expose wrong-offset and wrong-width
stores. The independent harness checks initial zero state, first use,
repeated writes, signed boundaries, and unchanged neighboring fields.

The exact Windows editor compiled the original source and built a Windows x64
Release IL2CPP player with zero errors; editor and player each passed eight
independent observations. Before the bounded proof, strict player-only source
recovery rejected the sole selected method. The proof now authenticates the
complete 59-byte native body, TypeInfo guard and initializer, preserved input,
own four-byte field store, unchanged layout, no managed class constructor and
no readonly target. Strict recovery emits the one method, pinned typed IL
verification passes, and both declaration comparisons have zero differences.
Regenerated source compiles and rebuilds in the exact Windows editor with zero
errors; original and recovered editor/player runs each pass all eight checks.
Allocator or metadata-initializer resource failures remain outside this finite
behavior probe.
