# Array access control

Build this synthetic fixture with Unity 2021.3.35f1 Windows x64 Release IL2CPP. Its methods read and write `int[]`, `uint[]`, `long[]`, and `ulong[]` elements. The independent harness covers empty and populated arrays, signed/unsigned boundary values, negative and excessive indices, and null arrays. All eight methods pass strict player-only recovery, typed IL verification, declaration comparison, exact Unity compilation and native behavior validation while preserving null and bounds exceptions in this bounded scope.
