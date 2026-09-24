# Float array element control

The two authored methods read and write `float[]` elements. The independent
behavior harness records every float as its 32-bit representation. It checks
positive and negative zero, both infinities, distinct quiet NaN payloads,
smallest and largest positive and negative subnormals, null and bounds
exceptions, aliasing after writes, and unchanged neighboring elements.

Build the original fixture with the supplied Unity 2021.3.35f1 Windows editor
as a Windows x64 Release IL2CPP player. Its source and original managed
assembly are validation oracles. A passing original build does not establish
recovered source compilation or behavioral fidelity.
