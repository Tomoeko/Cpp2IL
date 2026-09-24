# Nested Boolean getter control

This synthetic control isolates an instance property getter that reads a
Boolean field through one reference field: `Child.Flag`. The selected assembly
has two ordinary classes, their implicit constructors and the getter. The
harness checks false and true reads, repeated reads, a changed child reference,
neighboring fields, a null child and a null owner.

The original Unity 2021.3.35f1 Windows x64 Release IL2CPP player has a complete
eight-instruction, handler-free getter body: it loads the child reference,
branches to the authenticated runtime null throw when that reference is null,
then reads one Boolean byte and returns. Its unwind span ends in trap padding.
The proof requires the unchanged getter and field declarations, field layout,
unique native entry, exact control flow and file-backed executable bytes.

The frozen preproof player-only run rejected the getter at the unresolved null
guard. Current strict recovery emits all three selected methods. The emitted
getter IL reads `Child` and then `Flag`; generated C# reads `Child.Flag`.
Pinned typed IL verification and both declaration comparisons pass with zero
differences. Recovered source compiles in the supplied Windows editor and
rebuilds as a Windows x64 Release IL2CPP player with zero errors. Original and
recovered editor/player runs each pass all 15 observations, including both
null-receiver exceptions. These finite observations do not establish other
getter shapes or concurrent/volatile behavior. Raw receipts and logs stay under
ignored `Files/`.
