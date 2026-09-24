# Instance field array access fixture

The selected assembly defines an ordinary reference class with an `int[]`
field, a neighboring integer field, three authored access methods, and its
implicit constructor. The methods read element zero, read a dynamic index,
and write a dynamic index. Keep the constructor in the assembly-wide strict
recovery denominator, for four managed methods total.

The independent harness lives in `FieldArrayHarness`. Its oracle covers null
owners, null fields, empty and populated arrays, negative and high indices,
signed extremes, shared array references, field identity, and unchanged
neighbor state. A failed write must leave both the owner and alias views
unchanged. The observations compare exception types, not messages or stack
traces.

Build an original Windows x64 Release IL2CPP player with the supplied Unity
2021.3.35f1 editor before changing recovery. Keep source, generated C++,
managed assemblies, and symbols separate from player-only recovery inputs.
Record pre-change strict dispositions, then require typed IL, declaration
comparison, clean Unity source compilation, a Release IL2CPP rebuild, and
editor/player behavior for both original and recovered projects. Native
control flow, array identity, access width, guard order, helper exits, and
the complete unwind span must be established before replacing any guard.
