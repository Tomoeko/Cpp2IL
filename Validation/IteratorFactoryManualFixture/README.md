# Handwritten iterator factory control

An ordinary method creates an `IEnumerator` implementation with an integer state constructor and an owner field initializer. The implementation has simple, explicit interface methods, allowing the factory's native allocation, initialization and reference store to be investigated separately from compiler-generated `MoveNext` and `Reset` bodies.

The exact Windows Unity 2021.3.35f1 round trip passes strict player-only recovery of all seven selected methods, typed IL verification, zero-difference declaration comparisons before and after rebuilding, generated-source compilation and a Release IL2CPP native rebuild. Original and recovered editor/player runs each pass 13 observations. The ignored receipt is `Files/validation/iterator-factory-manual-roundtrip-final-01/roundtrip.json`.

The closed proof separately binds the metadata initializer, allocator, integer-state constructor, captured-owner field, write barrier and null helper. Compiler-generated iterators and malformed-metadata or resource-failure paths remain outside this accepted scope.
