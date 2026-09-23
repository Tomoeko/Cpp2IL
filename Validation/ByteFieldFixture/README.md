# Byte field equality fixture

This four-method assembly isolates ordinary byte-sized instance fields compared with zero: an implicit constructor, a conditional Boolean store, unsigned-byte equality and signed-byte equality. The independent driver covers all four initial Boolean pairs, every byte and signed-byte value, unchanged receiver fields and three null-receiver exceptions: 519 observations per process.

The `byte-fields` profile requires strict recovery of the complete assembly, typed managed IL verification, declaration comparison, exact Unity 2021.3.35f1 compilation, Windows x64 Release IL2CPP rebuilding and independent native behavior checks. It does not establish general narrow arithmetic, partial-register, volatile, packed or overlapping-field recovery. Those unresolved controls remain in the separate `NarrowFieldControls` assembly; no methods are removed from its denominator to make this positive fixture pass.

The local exact-target round trip passed all four selected methods, managed IL verification and both declaration comparisons with no differences. Recovered source compiled and rebuilt as Windows x64 Release IL2CPP; the original and rebuilt editor/player runs each passed all 519 observations. This result covers the finite cases above, not whole-program equivalence. Raw inputs, tool hashes, logs and receipts remain under ignored `Files/`.
