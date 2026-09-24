# Managed reference-field store control

Build this neutral fixture with Unity 2021.3.35f1 Windows x64 Release IL2CPP.
The selected authored method is a direct assignment of an unchanged class
reference parameter to an instance field: `owner.Next = value`. The implicit
`ReferenceNode` constructor is the only other selected method. The independent
harness tests new and replacement non-null values, clearing to null, assigning
null to an already-null field, assigning the existing value, assigning the owner
to its own field, and null owners with null and non-null values. It checks
reference identity, untouched adjacent reference fields and markers, and the
exception type. No volatile or side-effecting control method belongs to the
selected assembly.

Only the exact tested store shape can be accepted from this fixture. A fresh
Unity source compile, Release IL2CPP rebuild and editor/player behavior
comparison are required before recording recovered behavior.
