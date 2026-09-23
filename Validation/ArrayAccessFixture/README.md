# Array access control

Build this synthetic fixture with Unity 2021.3.35f1 Windows x64 Release IL2CPP. Its single method reads an `int[]` element. The independent harness covers empty and populated arrays, negative and excessive indices, and null arrays. The player-only recovery step must preserve the order and type of null and bounds exceptions. This is an investigation fixture until a complete strict recovery, Unity compilation and native behavior round trip passes.
