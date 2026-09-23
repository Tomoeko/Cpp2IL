# Narrow field provenance controls

This separate synthetic assembly investigates what Unity 2021.3.35f1 Windows x64 Release IL2CPP preserves about byte-sized field equality. Ordinary Boolean, byte and signed-byte fields are accompanied by volatile fields, explicit packing and overlapping fields. The linker file retains all controls even when the selected behavioral fixture does not call them.

Compare original managed field signatures with player metadata and native instructions. A zero `NumMods` value alone does not establish that a volatile modifier was absent. These controls are investigation inputs, not a claim that any field recovery is complete or behaviorally verified.

The float harness copies this companion assembly unchanged into original and replacement projects and hashes its files. It is outside the selected twelve-method `FloatComparisonFixture` recovery scope and its behavioral denominator. These authored controls must remain separate validation oracles if a later player-only narrow-field recovery is attempted.

The exact-target original build confirms that volatile field signatures retain `modreq(IsVolatile)` in the original managed oracle while player metadata reports zero modifiers for both ordinary and volatile fields. The volatile native reads contain a separate memory-barrier call before a register test; the ordinary controls compare byte memory directly with zero. Explicit packing and overlapping offsets survive in metadata, and the packed-class method shares a native body with an ordinary method. Modifier absence and layout cannot therefore be inferred from a zero modifier count or shared native bytes.
