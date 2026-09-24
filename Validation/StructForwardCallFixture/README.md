# By-value struct forwarded through a reference field

The selected assembly has three implicit class constructors and two authored
methods. `FloatPair` is a sequential eight-byte value type with two `float`
fields. `ForwardOwner.Forward` forwards that by-value argument to a uniquely
bound nonvirtual method through one ordinary reference field. Its second,
class-reference argument is unused. The target stores a copy of the pair and
returns a constant Boolean value, keeping target recovery separate from the
forwarding and null-guard question.

The separate harness checks exact input and copied field bits, unchanged
caller storage after forwarding, independence of the target's stored copy,
shared-target aliasing, ignored-object state, null receiver and null owner.
Vectors include signed zero, infinities, a quiet NaN,
a subnormal, and finite values. No source or managed oracle is supplied to
player-only recovery. The existing generic null-guard coalescer still excludes
non-enum value types.

The exact original Windows x64 Release IL2CPP build established a closed,
file-backed, handler-free guarded tail call. Its native instructions preserve
the entire eight-byte struct argument in RDX and overwrite the ignored class
argument register before the jump. The uniquely bound target stores that RDX
value as one eight-byte field. Before the bounded proof, strict player-only
recovery emitted four of the five selected methods and rejected only
`Forward` at the runtime null guard.

After adding the guarded eight-byte struct proof, the exact Unity 2021.3.35f1
roundtrip passed: strict selected recovery 5/5, pinned IL verification,
independent declaration comparisons with zero differences both before and
after the rebuild, Windows editor source compilation, Windows x64 Release
IL2CPP player rebuild, and original/recovered editor and player behavior
13/13 each. The proof requires the complete native shape, matching unwind and
file bytes, unique field and target binding, unchanged managed signatures,
and a sequential, two-float, eight-byte value type without static fields or
a static constructor. This is one bounded native behavior result; it does not
establish recovery of other struct layouts, value types with static
initialization, or general null-guarded calls. A paired broad audit measured
no new emitted methods under these limits.
