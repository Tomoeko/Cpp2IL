# Read/write instance reference property control

This neutral fixture adds an ordinary getter to one direct reference-property
setter while retaining a second setter-only property. The object and string
stores target distinct classes with an untouched neighboring reference and
integer field. The selected assembly has five managed methods: two implicit
constructors, two setters and one getter.

The control tests whether a read/write property setter can be bound to its own
complete native store and metadata even when the other setter shares its native
address. The getter has a separate required recovery path; a strict whole-
assembly result needs all five methods. The behavior harness checks getter
identity after initial assignment, replacement and null clearing, separate and
aliased receivers, neighboring fields, both accessor declarations and null
receiver exceptions. In the exact Windows Unity 2021.3.35f1 Release IL2CPP
control, both setters share the proved native store. Strict player-only
recovery emits all five methods with valid typed IL and zero differences at
both declaration comparisons. Fresh generated C# compiles and rebuilds in
that editor; original and recovered editor/player runs each pass all 34
observations. This is a bounded property-store result, not a general proof
for other native stores or concurrent behavior.
