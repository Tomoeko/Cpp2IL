# Constructor thunk chain control

This synthetic fixture has two parallel, parameterless constructor chains. The
two root constructors perform the same observable field write and are marked
`NoInlining`. Their middle and leaf classes have no declared fields or
constructor body statements. The separate behavior harness checks the inherited
write, default fields, fresh objects and reference identity.

The intended native control is narrow: each leaf constructor and its immediate
middle constructor must share one complete, file-backed seven-byte Windows x64
Release IL2CPP entry, `xor edx, edx; jmp T`. The target `T` must bind an older
ancestor constructor while binding neither leaf nor immediate middle. The
metadata must show one public parameterless constructor for each owner and
base. A compiler result that differs from this shape is **not** evidence for
the intended constructor-call proof.

The exact Unity 2021.3.35f1 Windows Release IL2CPP control produced this shape:
both leaf and both middle constructors share the complete seven-byte entry,
which targets the two root constructors' shared 29-byte body. That body calls
the Object constructor target directly and writes the inherited Int32 field.
The root types' sole immediate base has a separately proved inert constructor
thunk to Object, so recovered IL calls that immediate base before the field
write. These native and metadata facts are required gates for the narrow
recovery.

A fresh player-only roundtrip recovered all seven fixture constructors without
detected degradation. Typed managed IL verification and both stripped and rebuilt
declaration comparisons passed with zero differences. Original and recovered
source compiled in the exact Windows editor, produced Win64 Release IL2CPP
players, and passed all 11 editor and 11 player behavior observations each.
This fixture validates that bounded shape and behavior; it does not establish
original source syntax beyond what the player preserves.
