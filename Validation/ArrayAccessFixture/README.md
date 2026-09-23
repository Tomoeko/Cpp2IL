# Array access control

Build this synthetic fixture with Unity 2021.3.35f1 Windows x64 Release IL2CPP. Its methods read and write an `int[]` element. The independent harness covers empty and populated arrays, negative and excessive indices, and null arrays. Both methods have passed strict player-only recovery, exact Unity compilation and native behavior validation while preserving null and bounds exceptions in this bounded scope.
