# Narrow array element control

The four authored methods read and write `byte[]` and `sbyte[]` elements. The
separate behavior harness checks unsigned values 0, 127, 128 and 255; signed
values -128, -1, 0 and 127; null, empty and out-of-range accesses; aliasing;
and complete array contents after successful and failed writes.

Build the original fixture with the supplied Unity 2021.3.35f1 Windows editor
as a Windows x64 Release IL2CPP player. Its source and original managed
assembly are validation oracles. A passing original build does not establish
recovered source compilation or behavioral fidelity.
