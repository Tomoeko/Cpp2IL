# Inherited string field and literal concatenation control

`Resolver.Compose` calls a protected nonvirtual method inherited from
`ResolverBase`, reads the returned object's public `Text` field inherited from
`TextNode`, and concatenates a string literal. `NoInlining` keeps the two method
bodies available for separate recovery. The runner observes a null returned
node, a null caller, repeated literal use, inherited field access, virtual
dispatch through a base-typed reference, and unchanged neighboring fields.

The selected assembly contains five implicit constructors, `Lookup`, and
`Compose`. The exact Unity 2021.3.35f1 Windows x64 Release IL2CPP round trip
passes strict recovery of all seven methods from isolated player binary and
metadata, typed IL verification, and zero-difference declaration comparisons
before and after rebuilding. Fresh generated source compiles in the supplied
Windows editor, and the recovered project builds a Release IL2CPP player with
zero errors. Original and recovered editor and player runs each pass ten
independent observations. The ignored local receipt is
`Files/validation/literal-concat-roundtrip-01/roundtrip.json`.

The recovery proof covers this complete native shape: a uniquely compatible
inherited getter, an inherited string field read, a metadata-backed literal,
the authenticated initialization and null helpers, and an exact
`String.Concat(string, string)` tail call. A later strict source-only run with
the final helper checks emitted all seven methods and produced six generated
source and project configuration files byte-identical to the accepted
roundtrip. Other concatenation and helper shapes remain unresolved.
The ten observations do not verify literal-cache allocation races, resource
failures, or exception message and stack details. Runtime exports anchor the
accepted helper identities, but their complete implementations are not lifted.
