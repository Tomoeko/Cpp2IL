# Runtime class cast and literal concatenation control

This synthetic fixture isolates four native behaviors in one assembly:

1. `AppendLiteral` loads a metadata-backed string literal for concatenation.
2. `TargetType` requests metadata for `DerivedNode` without a class cast.
3. `Lookup` casts an ordinary base-class reference field to `DerivedNode`.
4. `Compose` calls that lookup, reads an inherited string field, and appends the
   literal through a virtual caller.

The independent behavior oracle checks null and incompatible references,
exact and subclass matches, returned object identity, repeated literals,
virtual dispatch, aliasing, and unchanged neighboring fields. The fixture is
intended to identify whether the supplied Unity 2021.3.35f1 Windows x64
Release IL2CPP build produces the closed native shapes under investigation.
Matching source semantics alone does not establish a native-shape match or
successful player-only recovery.

The first original build passed source compilation and a Windows x64 Release
IL2CPP player build in the supplied editor. Its editor and player each passed
23 independent behavior observations. The virtual `Compose` body has the
intended 20-instruction caller sequence, but `Lookup` has a 37-instruction
class-cast body, one zero-extension instruction shorter than the separate
38-instruction audit candidate. The build's runtime metadata initializer is
already accepted by the existing helper proof. These are negative controls
for the alternate helper and exact cast shape, not evidence that either is
recovered. Initial strict recovery from the isolated player inputs emits the
six implicit constructors and rejects all four explicit methods. The local
original-build receipt is under
`Files/validation/runtime-cast-concat-baseline-01/receipt.json`.
