# 16-bit array widening control

The two authored methods read `short[]` and `ushort[]` elements as `int`.
The separate behavior harness checks signed and unsigned extrema, negative
and upper-bound indices, null arrays, and complete unchanged array contents.

Build the original fixture with the supplied Unity 2021.3.35f1 Windows editor
as a Windows x64 Release IL2CPP player. Its source and original managed
assembly are validation oracles. A passing original build does not establish
recovered source compilation or behavioral fidelity.
