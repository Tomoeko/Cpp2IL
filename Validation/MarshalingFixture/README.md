# Marshaling metadata fixture

This separate synthetic assembly compares Boolean fields declared with
`MarshalAs(I1)`, `MarshalAs(U1)`, `MarshalAs(Bool)`, and no explicit descriptor.
Signed byte, unsigned byte, integer, and Boolean fields provide unannotated
controls. It does not alter the declaration fixture's 35-method denominator.

Copy this directory into its own subdirectory beside the arithmetic fixture
when staging a fresh source directory for `Validation/run_fixture.py`. Use the
exact Windows x64 Release IL2CPP profile, then inspect the native metadata's
`fieldMarshaledSizes` entries and resolve each entry's type index using that
player's type registration. Keep original managed descriptors separate as
validation oracles. Preserve the triples, resolved types, missing entries, and
original managed declarations in ignored evidence.

A recorded byte size alone does not identify the original marshaling
descriptor. Even if the cases here differ in their metadata entries, that
does not establish uniqueness for other supported or unsupported descriptors.
The arithmetic harness's behavior result makes no marshaling-behavior claim
for this assembly.

The controlled Unity 2021.3.35f1 Windows x64 Release build produced:

| Original Boolean declaration | Table type | Recorded bytes | HasFieldMarshal |
| --- | --- | --- | --- |
| No descriptor | System.Boolean | 4 | No |
| MarshalAs(I1) | System.Boolean | 1 | Yes |
| MarshalAs(U1) | System.Boolean | 1 | Yes |
| MarshalAs(Bool) | System.Boolean | 4 | Yes |

The I1 and U1 cases share the same registered field type and table type. Their
original managed descriptor blobs differ, so this is a demonstrated collision
in the available type/size/flags evidence. Table entries also exist for an
unannotated Boolean; entry presence does not imply an explicit descriptor.
Unannotated byte, signed byte, and integer controls had no table entries.
All five types and twelve fields survived stripping, and the separate
declaration fixture remained unchanged.
