# Instance-field null guard control

Build this synthetic fixture with Unity 2021.3.35f1 Windows x64 Release IL2CPP. Its methods read and write one signed `int` field on a sealed reference class. The independent harness covers null and populated receivers, including signed integer boundaries. Both bounded scopes require player-only recovery to preserve field values, write effects and null exceptions through exact Unity compilation and native validation.
