# Attribute Type qualification control

This separate, synthetic assembly isolates one nonnull `System.Type` custom
attribute argument. Its lone authored constructor has no behavior beyond the
base attribute constructor. The linker file retains the declaration in an
exact Unity 2021.3.35f1 Windows x64 Release IL2CPP player.

Stage it beside `Validation/Fixture` in an ignored `Files/` source directory
and build through `Validation/run_fixture.py` with `--profile arithmetic`.
Player-only recovery must report `SOURCE010` for the selected assembly because
metadata v29 retains the type index but not the original serialized Type-name
qualification. A strict source export must reject that unresolved declaration
even if its one method body is recovered. The original managed assembly is a
validation oracle only, not a recovery input.
