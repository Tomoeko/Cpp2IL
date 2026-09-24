# Static initialization boundary for a forwarded value type

This separate synthetic control adds two static fields and an explicit static
constructor to an eight-byte sequential value type with two `float` instance
fields. Its guarded caller forwards the value by copy through a reference
field and ignores a second class argument. A separate witness records whether
creating a default value or forwarding it causes static initialization before
any static member of the value type is read. The harness also checks the
explicit static read, bitwise copies, ignored argument and null owner/receiver.

The exact original Windows Unity 2021.3.35f1 source compiles and builds a
Windows x64 Release IL2CPP player. Both original editor and player runs pass
six independent observations: default-value creation, null owner/receiver
and successful forwarding leave the initialization witness at zero; an
explicit static read changes it to one; another forwarding call leaves it at
one. This agrees with the [C# struct static-constructor rule](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/structs#16810-static-constructors).
The caller has the same complete, file-backed, handler-free eight-instruction
tail-call shape as the accepted simple fixture, and its unique target stores
the whole eight-byte argument from RDX.

The current bounded proof requires a value type without static fields or a
static constructor. A strict player-only source attempt emits four of six
selected methods and rejects `Forward` at the runtime null guard. The struct
static constructor independently fails at an unsupported native byte-sized
metadata-flag comparison. No recovered source compilation, native rebuild or
behavioral equivalence is claimed for this variant. The original source and
managed assemblies remain validation oracles only; they were not supplied to
player-only recovery.
