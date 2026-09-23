# Metadata literal guard fixture

This separate one-method assembly validates the positive metadata-only guard proof without silently excluding the unresolved Boolean, byte, signed, word and class-initialization controls in `NarrowComparisonFixture`.

Use the `metadata-literal` profile with the exact supplied editor. The independent harness calls the method twice in a fresh editor/player process and requires the declared neutral string and the same object identity each time. A successful recovery still requires managed IL verification, declaration comparison, clean Unity source compilation, Windows x64 Release IL2CPP rebuilding and the repeated native observations.

The exact-target round trip has passed those independent gates for its one selected method: strict recovery without detected degradation, typed IL verification, zero recovered and rebuilt declaration differences, and two matching observations in both original and rebuilt players. This finite result does not establish general metadata-guard or whole-program equivalence.

The native method-size estimate in this fixture includes unreachable padding and neighboring code. Guard removal therefore proves a closed entry path through the same literal load and return; it does not truncate arbitrary methods at their first return or ignore reachable branch targets.
