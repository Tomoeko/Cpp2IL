# Array-returning direct-call control

Build this neutral fixture with Unity 2021.3.35f1 Windows x64 Release IL2CPP. A static wrapper calls an instance method that returns its supplied array and increments a field. The harness checks reference identity, element contents, the field effect including signed overflow, empty and null arrays, and null receivers. This control tests whether a null guard and native tail call can be represented by a managed call that preserves array argument, return and side-effect semantics.
