# Static field getter fixture

Build this neutral assembly with Unity 2021.3.35f1 for Windows x64 Release
IL2CPP. Three ordinary, nongeneric static getters return a native-sized
integer, a concrete class reference, and a Boolean from the same type. A second
Boolean field neighbors the returned field; the static owner has no class
constructor. The independent harness changes the fields, checks exact pointer
values and reference identity, and observes both Boolean values while its
neighbor holds the opposite value after explicit assignments. It exercises the initial metadata guard and
later calls.

Only an exact file-backed getter body with a resolved TypeInfo slot, verified
metadata-initialization guard, unique static field offset, matching return
type, handler-free unwind, and no class constructor is eligible for the
bounded recovery. The Boolean getter additionally needs an exact byte-load and
zero-extension proof; its native shape is not assumed from this source. Count
it only after strict player-only recovery, typed IL verification, declaration
comparisons, exact Unity source compilation, Windows x64 Release IL2CPP
rebuild, and original/recovered behavior checks.
