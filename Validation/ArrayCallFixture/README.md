# Array-returning direct-call control

Build this neutral fixture with Unity 2021.3.35f1 Windows x64 Release IL2CPP. Static wrappers call a base-class instance method through both base and derived receivers. A second wrapper calls it twice through separate receivers, so the harness checks null-failure order and field effects. An instance wrapper loads both the callee and its array argument from separate fields before a guarded tail call. The target method returns its supplied array and increments a field. The harness checks reference identity, element contents, the field effect including signed overflow, empty and null arrays, null receivers, and a null wrapper owner. This control tests whether native guards and tail calls can be represented by managed calls that preserve array argument, return and side-effect semantics across an unchanged base-class chain.

`ArrayCalls.ReadFromEcho` is a new call-result array-access candidate. It calls
the no-inline `ArrayEcho.ReturnConfigured` and then reads one `int` from its
result. The callee increments `Calls` and returns its `ReturnedValues` field;
the original `Echo` method and its identity controls remain unchanged. The
argument and returned arrays can contain different values and lengths, and
either can be null independently. The harness checks these distinctions,
null receiver and bounds failures, unchanged arrays, and whether the counter
increment precedes a returned-array failure, including signed wraparound.
A fresh original player and player-only recovery established the bounded native
shape. The strict run emitted all 10 selected methods. Pinned typed IL checks,
declaration comparison against the original stripped oracle, compilation in
the supplied Windows editor, a nondevelopment Win64 Release IL2CPP rebuild,
and 30 matching observations in each original/recovered editor/player stage
passed. This does not establish arbitrary call-result array access.
