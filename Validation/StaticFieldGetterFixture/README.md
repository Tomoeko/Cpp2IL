# Static field getter fixture

Build this neutral assembly with Unity 2021.3.35f1 for Windows x64 Release
IL2CPP. The two ordinary, nongeneric static getters return a native-sized
integer and a concrete class reference from the same type. The static owner has
no class constructor.
The independent harness changes both fields, checks exact pointer values and
reference identity, and exercises the initial metadata guard and later calls.

Only an exact file-backed getter body with a resolved TypeInfo slot, verified
metadata-initialization guard, unique static field offset, matching return
type, handler-free unwind, and no class constructor is eligible for the
bounded recovery. Count it only after strict player-only recovery, typed IL
verification, declaration comparisons, exact Unity source compilation,
Windows x64 Release IL2CPP rebuild, and original/recovered behavior checks.
