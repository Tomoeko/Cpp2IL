# Enum passthrough fixture

Build this neutral assembly in Unity 2021.3.35f1 for Windows x64 Release IL2CPP.
`EnumForwarder.Forward` loads an ordinary reference field, guards the receiver,
and forwards one unchanged `int`-backed enum parameter to a nonvirtual method.
The receiver records the signed underlying value after an invertible
alternating-bit XOR in a field after a neighboring wide field. The independent
harness checks the transformed bits, unchanged neighboring field, and both
null paths.

Only the exact same-enum wrapper is eligible for the bounded guarded-call proof.
An integer-to-enum conversion or a different enum type is not interchangeable
with its signature, even when optimized native instructions use the same
32-bit argument register. Count recovery only after strict player-only method
coverage, typed IL verification, both declaration comparisons, exact Unity
source compilation, a Windows x64 Release IL2CPP rebuild, and independent
original/recovered editor and player behavior checks all pass.
