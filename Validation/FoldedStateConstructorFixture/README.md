# Folded state constructor control

This synthetic fixture has two sealed direct children of `System.Object`.
Each has one public constructor accepting `Int32`, an `Int32` state field and
an untouched neighboring reference field. The two constructor bodies are
intentionally identical so the exact Windows x64 Release IL2CPP build can
fold them to one native address. The selected assembly has exactly two method
identities; the behavior harness is a separate assembly.

The independent oracle checks signed boundary values, fresh instances,
receiver aliases, reflection construction, exact declarations and untouched
neighbor references. The fresh original Unity 2021.3.35f1 Windows editor
compilation and Windows x64 Release IL2CPP native build passed; the native
BuildReport has zero errors. Independent editor and player runs each passed
8/8 observations.

Both constructors fold to one native entry with nine recorded managed aliases.
The authoritative handler-free unwind region covers 36 file-backed bytes and
12 instructions. It calls a separately authenticated inert `System.Object`
constructor, then writes the incoming `Int32` to the receiver's unchanged field
at offset 16; the neighboring reference starts at offset 24. A metadata-based
raw-body estimate extends to 252 bytes and includes subsequent native code, so
the complete function must be bounded by its unwind entry. The 36-byte
instruction template matches an independently inspected diagnostic cohort,
although that player's native entry has more aliases.

The pinned preproof strict player-only source attempt exited with status 1:
both selected constructors Failed on ambiguous shared native call identity
(0 Emitted / 2 Failed). It produced no recovered Unity rebuild or recovered
behavior result.

Native address sharing alone does not identify a managed call target. A
bounded proof now binds the immediate `System.Object` base constructor and
unchanged `Int32` field through metadata, complete native bytes and unwind.
It rejects output overrides, changed call or field identities, altered trap
padding and a jump or shifted entry across the 36-byte unwind boundary.
Strict player-only recovery emits both constructors. Pinned typed IL
verification and both original-oracle declaration comparisons pass with zero
differences. Fresh generated source compiles in the exact Windows editor,
rebuilds as Windows x64 Release IL2CPP with zero errors, and matches the
original on all eight editor and player observations. Other constructor shapes
and the similar broad-player cohort remain unverified behavior.
