# Direct-state iterator factory control

A public handwritten `IEnumerator` has an empty parameterless constructor as a
control. Its one-argument constructor writes an integer state, and the factory
writes the captured owner. This isolates an exact-target Release IL2CPP
iterator-allocation variant without requiring compiler-generated state-machine
declarations or bodies to be recovered.

The independent harness observes fresh instances, both field writes, interface
behavior, unchanged neighbors and a null-owner call. In the exact Windows Unity
2021.3.35f1 Release IL2CPP build, the factory has a 28-instruction, 112-byte
body. Its one-argument constructor has 12 instructions. Strict player-only
recovery emitted all eight selected methods; typed IL verification, declaration
comparison before and after rebuilding (zero differences), an exact Unity
rebuild, and all 13 original and recovered editor and player observations
passed. The local accepted receipt is under
`Files/validation/iterator-factory-variant-roundtrip-02/roundtrip.json`.

This fixture proves only its bounded native shape. A related 113-byte shape in
other player inputs currently fails the separate runtime metadata initializer
helper proof and reverses the state/owner native field-write order. It remains
unsupported pending independent authentication of both differences. Those
players' IL2CPP Release build configuration is also unverified.
