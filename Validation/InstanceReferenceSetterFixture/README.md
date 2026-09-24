# Direct instance reference setter control

This synthetic control has two ordinary classes with setter-only
properties. Each setter writes its incoming reference to a directly declared
field after an untouched neighboring reference. One field is `object` and the
other is `string`; the classes have no explicit static constructors. The
behavior harness is in a separate assembly and checks reference identity,
replacement, null clearing, separate and aliased receivers, adjacent fields,
null-receiver exceptions, and the declared property types.

The selected assembly has four managed methods: two implicit constructors and
two setters. Its original source compiles in the supplied Unity 2021.3.35f1
Windows editor and builds a Windows x64 Release IL2CPP player with zero errors.
Both editor and player pass 25 independent observations. The setters share one
complete 12-byte native leaf: add the receiver's field offset, store the input
reference, then tail-transfer through direct jump thunks to the authenticated
GC card marker. The original build used the .NET 4.x API profile, Low stripping,
OptimizeSpeed, and nondevelopment settings. Strict player-only recovery emits
all four selected methods, including both setters. Typed IL verification and
both independent declaration comparisons pass with zero differences. Clean
generated source compiles in the exact Windows editor and rebuilds as a Windows
x64 Release IL2CPP player with zero errors. Original and recovered editor and
player runs each pass the same 25 independent observations. This is finite
behavioral acceptance for the proved native shape, not general setter support.

The motivating independent diagnostic player contains a repeated three-
instruction leaf: adjust the receiver to a reference field, store the incoming
reference, then tail-transfer to the installed GC card marker. Its original
project did not finish a clean Unity build, so those player-only findings are
structural leads rather than behavioral acceptance. The neutral control proves
the same native shape in a complete exact-target build. Volatile, readonly,
inherited, value-type and metadata-mismatched fields remain outside the proved
positive scope; concurrent behavior is unverified.
