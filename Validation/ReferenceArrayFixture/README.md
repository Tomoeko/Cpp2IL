# Reference array element read control

Three authored methods read an `object`, `string`, or ordinary class from a
single-dimension reference array. The independent behavior harness checks
element identity, null elements, covariant arrays, unchanged contents, null
arrays, and negative and upper-bound indices.

Build the original fixture with the supplied Unity 2021.3.35f1 Windows editor
as a Windows x64 Release IL2CPP player. Its source and managed assembly are
validation oracles. A successful original build alone does not establish
recovered-source compilation or behavior.
