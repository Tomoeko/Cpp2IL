# Direct static-field forwarding control

This neutral fixture directly reads another type's static `object` field after
an observable, non-inlined `Prepare()` call, then passes the field to one of
two distinct static receivers. The field owner has no static constructor.
`Prepare()` can replace the field or throw; both effects must precede the field
read and receiver call in the managed source.

The intended runner profile is `metadata-forwarding`, assembly
`MetadataForwardingFixture`, with five selected methods. Its harness checks
eleven declaration, cold, repeated, mutation, null, signed-argument and
exception observations.

## Exact original control

A fresh Unity 2021.3.35f1 Windows editor run compiled this source, built a
Windows x64 Release IL2CPP player with zero errors, and passed all eleven
observations in both editor and player. The two forwarding native methods are
complete file-backed, handler-free roots of fourteen and eighteen instructions.
Their generated C++ and native bodies initialize the field owner's TypeInfo
**before** calling `Prepare()`, despite the C# statement order. The runtime
metadata helper satisfies the existing exact-profile proof.

A player-only strict diagnostic completed analysis but rejected all five
selected methods. Both forwarding methods had unresolved native flag values
and metadata-initialization effects. `Prepare()` had an unsupported native
exception-region boundary; the two receiver methods had unresolved native
flag values and narrow integer operations. No recovered source compilation or
rebuild was attempted for this control.

This observed order makes the direct field access a negative control for a
Prepare-before-guard native wrapper. It does not authenticate other metadata
helper variants or establish recovered behavior.
