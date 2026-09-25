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

An independent exact-target experiment selected `OptimizeSize` while keeping
the Windows x64 IL2CPP compiler configuration at `Release`. Its factory has a
113-byte native unwind span. The first strict attempt rejected it because the
generated once flag was a writable, file-backed zero byte rather than part of
the virtual zero-initialized section. The proof now accepts this storage form
only after checking the on-disk zero and that the PE loader cannot relocate the
byte. A fresh player-only round trip strictly emitted all eight selected
methods, passed typed IL verification and stripped-oracle declaration
comparison with zero differences, compiled and rebuilt in the supplied Windows
editor, and matched all 13 observations in each original and recovered editor
and player stage. The ignored passing receipt is under
`Files/runs/iterator-factory-optimize-size-roundtrip-02/`.

The original `OptimizeSpeed` path was rerun after the relocation check changed.
It again passed 8/8 strict recovery, typed IL, both zero-difference declaration
comparisons, exact Windows compilation and Release rebuild, and 13 matching
observations in every editor/player stage. Its ignored receipt is under
`Files/runs/iterator-factory-speed-regression-01/`. This repeated control is
not added to the roadmap's bounded round-trip total.

The metadata helper in this controlled build still has the proven `0x5d`
first span. This run does not authenticate the alternate `0x37` helper seen in
other inputs.
