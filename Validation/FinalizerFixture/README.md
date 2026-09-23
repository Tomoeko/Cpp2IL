# Finalizer relationship fixture

This separate synthetic assembly contains a destructor, an inherited finalizer,
a derived destructor, an ordinary virtual override, a new-slot virtual method,
and a method named Finalize with a different signature. It preserves the
existing declaration fixture's denominator.

Stage this directory beside the arithmetic fixture under a fresh ignored source
directory and build using `Validation/run_fixture.py` with the exact Windows
x64 Release IL2CPP profile. Keep original managed assemblies as separate
oracles. Compare both original stripped and unstripped metadata and inspect
the native type's finalizer bit, method flags, signature, slot, and vtable body.

Finalizer relationships are separate from interface mappings. An ordinary
same-name virtual override does not establish whether the original managed
assembly contained an explicit MethodImpl row. Do not add rows for every
implicit class override or infer a finalizer from its name alone.

This fixture does not request collection or depend on finalizer scheduling.
The arithmetic harness's behavior result does not verify destructor behavior.
