# Instance-field null guard control

Build this synthetic fixture with Unity 2021.3.35f1 Windows x64 Release IL2CPP. Its read method accesses one signed `int` field on a sealed reference class. The independent harness covers null and populated receivers, including signed integer boundaries. This bounded scope is accepted only when recovery preserves both the null exception and returned field value.
