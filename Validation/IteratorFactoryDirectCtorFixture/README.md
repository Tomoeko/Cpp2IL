# Direct-constructor iterator factory control

This synthetic control derives from the accepted direct-state iterator fixture.
Its factory creates an `IEnumerator`, passes zero to a noinline one-argument
constructor, then captures the owner in a separate reference field. An
untouched neighboring reference makes the intended owner field boundary
observable. The selected assembly has eight managed methods; the behavior
harness lives in a separate assembly.

The supplied Unity 2021.3.35f1 Windows editor compiled the original source and
built a non-development Windows x64 Release IL2CPP player with zero build
errors. Independent editor and player oracles each passed 21/21 observations
for signed state boundaries, fresh instances, owner capture, untouched neighbor,
interface dispatch and a null owner call.

The factory has a complete 108-byte, 28-instruction native body in a 109-byte
handler-free unwind region ending with one `INT3`. Its TypeInfo slot resolves
the allocated iterator; the direct call targets that iterator's one-argument
constructor, which has a separately bounded 36-byte body. The state field is
at offset 16, the untouched neighbor at 24 and the owner reference at 32.
This is a distinct direct-constructor shape from the earlier factory control,
which inlines the state write. The pinned preproof strict player-only source
attempt emitted 7/8 selected methods and failed the factory on an uncoalesced
runtime null guard. The bounded direct-constructor proof now emits all eight
from isolated player inputs. Pinned typed IL verification and declaration
comparisons against the stripped original managed oracle pass with zero
differences. Regenerated source compiles in the exact Windows editor and builds
a Windows x64 Release IL2CPP player with zero errors. Original and recovered
editor/player runs each pass the 21-observation oracle. These checks do not
establish every allocator or out-of-memory exceptional path.
