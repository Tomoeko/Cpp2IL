# Class-cast lookup control

The single explicit method reads an ordinary base-class reference field and
returns it as a derived class when the runtime type is compatible. The source
uses `as`; the recovery target is the evidenced reference-preserving result
and null behavior, not a claim that native code uniquely identifies C# syntax.

The behavior oracle covers null and incompatible field values, exact and
subclass matches, reference identity, aliases, a null owner, and unchanged
neighbor fields. This small fixture isolates the class-cast lookup from the
separate literal, TypeInfo-only, and combined-caller controls in the broader
runtime-cast fixture.

The bounded 37-instruction lookup now passes strict player-only recovery for
all five selected fixture methods. Pinned ILVerify and both independent
declaration comparisons pass. Regenerated source compiles in the supplied
Windows Unity 2021.3.35f1 editor, rebuilds as Win64 Release IL2CPP, and passes
all 12 observations in both editor and player; the original editor/player also
pass the same oracle. The separate 38-instruction register variant has a
matching native shape, but its alternate metadata initializer remains
unproved in the broad audit. The local accepted receipt is under
`Files/validation/class-cast-lookup-roundtrip-02/roundtrip.json`.
