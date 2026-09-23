# Metadata literal guard fixture

This separate one-method assembly validates the positive metadata-only guard proof without silently excluding the unresolved Boolean, byte, signed, word and class-initialization controls in `NarrowComparisonFixture`.

Use the `metadata-literal` profile with the exact supplied editor. The independent harness calls the method twice in a fresh editor/player process and requires the declared neutral string and the same object identity each time. A successful recovery still requires managed IL verification, declaration comparison, clean Unity source compilation, Windows x64 Release IL2CPP rebuilding and the repeated native observations.
